using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Models;
using LibGit2Sharp;
using Moq;
using Polly;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Git;

/// <summary>
/// Tests that RepositoryGitOperations.Push re-fetches a fresh token on each Polly retry attempt
/// when a token factory is supplied, and that 403 auth errors are treated as transient
/// (retryable) when a factory is present.
///
/// Root cause documented in run 1bfcbd88 (issue #1916): the token was captured once before the
/// Polly lambda, so a stale GitHub App installation token (expires after 1h) was reused across
/// all retry attempts in a ~5h run, causing every attempt to 403.
/// </summary>
public class RepositoryGitOperationsPushRetryTests
{
    /// <summary>
    /// When a 403 occurs and a token factory is provided, Push must retry with a freshly-fetched token.
    /// Before the fix: Push accepts a plain string token captured once; auth errors throw
    /// InvalidOperationException which Polly does not retry → this test fails to compile (missing
    /// factory overload) and will fail at runtime until the fix is applied.
    /// </summary>
    [Fact]
    public async Task Push_WithTokenFactory_RetriesOnAuthFailureAndFetchesFreshToken()
    {
        // Arrange
        var tokenCallCount = 0;
        var tokens = new[] { "expired-token", "fresh-token" };
        Task<string> TokenFactory(CancellationToken ct)
        {
            var token = tokens[Math.Min(tokenCallCount, tokens.Length - 1)];
            tokenCallCount++;
            return Task.FromResult(token);
        }

        // Pipeline: no outer timeout cap, 1 retry (2 total attempts), no delay for test speed
        var pipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(
            Log.Logger,
            timeout: TimeSpan.FromSeconds(10),
            outerTimeout: TimeSpan.FromSeconds(30));

        // Mock the actual network push: first call 403, second call succeeds
        var pushCallCount = 0;
        var usedTokens = new List<string>();

        // We test the token factory behaviour in isolation using a fake push action
        // that records which token was used and fails on the first attempt.
        var fakePushAction = new Func<string, Task>(usedToken =>
        {
            usedTokens.Add(usedToken);
            pushCallCount++;
            if (pushCallCount == 1)
            {
                // Simulate 403 from LibGit2Sharp via OnPushStatusError path
                throw new LibGit2SharpException(
                    "Push failed for ref 'refs/heads/feature': unexpected http status code: 403");
            }
            return Task.CompletedTask;
        });

        // Act — call the new overload that accepts a token factory
        await RepositoryGitOperations.PushWithTokenFactory(
            tokenFactory: TokenFactory,
            tokenUsername: "x-access-token",
            pushAction: fakePushAction,
            pipeline: pipeline,
            ct: CancellationToken.None);

        // Assert: two attempts were made
        pushCallCount.Should().Be(2);
        // Each attempt fetched a fresh token
        tokenCallCount.Should().Be(2);
        // First attempt used the expired token, second used the fresh one
        usedTokens.Should().Equal("expired-token", "fresh-token");
    }

    /// <summary>
    /// When no token factory is supplied (static token path), a 403 must NOT be retried —
    /// retrying with the same stale token would just fail again anyway.
    /// </summary>
    [Fact]
    public async Task Push_WithStaticToken_DoesNotRetryOnAuthFailure()
    {
        var pipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(
            Log.Logger,
            timeout: TimeSpan.FromSeconds(10),
            outerTimeout: TimeSpan.FromSeconds(30));

        var pushCallCount = 0;
        var fakePushAction = new Func<string, Task>(token =>
        {
            pushCallCount++;
            throw new LibGit2SharpException(
                "Push failed for ref 'refs/heads/feature': unexpected http status code: 403");
        });

        // Act — static token overload: no factory, so auth errors are permanent
        Func<Task> act = () => RepositoryGitOperations.PushWithTokenFactory(
            tokenFactory: _ => Task.FromResult("static-token"),
            tokenUsername: "x-access-token",
            pushAction: fakePushAction,
            pipeline: pipeline,
            retryOnAuth: false,       // static token path — auth errors are not retryable
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*authentication error*");

        // Not retried — exactly 1 push attempt
        pushCallCount.Should().Be(1);
    }

    /// <summary>
    /// Network errors are retried regardless of whether a token factory is used.
    /// </summary>
    [Fact]
    public async Task Push_WithTokenFactory_RetriesNetworkError()
    {
        var tokenCallCount = 0;
        Task<string> TokenFactory(CancellationToken ct)
        {
            tokenCallCount++;
            return Task.FromResult("token");
        }

        var pipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(
            Log.Logger,
            timeout: TimeSpan.FromSeconds(10),
            outerTimeout: TimeSpan.FromSeconds(30));

        var pushCallCount = 0;
        var fakePushAction = new Func<string, Task>(token =>
        {
            pushCallCount++;
            if (pushCallCount <= 2)
                throw new LibGit2SharpException("connection timed out");
            return Task.CompletedTask;
        });

        await RepositoryGitOperations.PushWithTokenFactory(
            tokenFactory: TokenFactory,
            tokenUsername: "x-access-token",
            pushAction: fakePushAction,
            pipeline: pipeline,
            ct: CancellationToken.None);

        // 3 total attempts (1 initial + 2 retries)
        pushCallCount.Should().Be(3);
    }

    /// <summary>
    /// Branch-protection errors are never retried even with a token factory.
    /// </summary>
    [Fact]
    public async Task Push_WithTokenFactory_DoesNotRetryBranchProtection()
    {
        var pipeline = ResiliencePipelineFactory.CreateGitNetworkPipeline(
            Log.Logger,
            timeout: TimeSpan.FromSeconds(10),
            outerTimeout: TimeSpan.FromSeconds(30));

        var pushCallCount = 0;
        var fakePushAction = new Func<string, Task>(token =>
        {
            pushCallCount++;
            throw new LibGit2SharpException("GH006: Protected branch update failed");
        });

        Func<Task> act = () => RepositoryGitOperations.PushWithTokenFactory(
            tokenFactory: _ => Task.FromResult("token"),
            tokenUsername: "x-access-token",
            pushAction: fakePushAction,
            pipeline: pipeline,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*protected*");

        pushCallCount.Should().Be(1);
    }
}
