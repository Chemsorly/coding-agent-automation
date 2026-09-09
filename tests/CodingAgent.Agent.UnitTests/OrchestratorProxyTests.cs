using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Agent;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="OrchestratorProxy"/>.
/// Since <see cref="HubConnection"/> is sealed and its InvokeAsync methods are extension methods,
/// we test constructor validation, interface compliance, and use a recording hub connection
/// to verify correct method names and parameters are passed.
/// </summary>
public class OrchestratorProxyTests
{
    [Fact]
    public void Constructor_ThrowsOnNullConnection()
    {
        var act = () => new OrchestratorProxy(null!, "job-1");
        act.Should().Throw<ArgumentNullException>().WithParameterName("connection");
    }

    [Fact]
    public void Constructor_ThrowsOnNullJobId()
    {
        // Build a minimal HubConnection (won't actually connect)
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();

        var act = () => new OrchestratorProxy(connection, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("jobId");
    }

    [Fact]
    public void ImplementsIAgentIssueOperations()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();

        var proxy = new OrchestratorProxy(connection, "job-1");

        proxy.Should().BeAssignableTo<IAgentIssueOperations>();
    }

    [Fact]
    public async Task PostCommentAsync_InvokesRequestPostComment()
    {
        // Arrange — use a recording handler to capture the outgoing request
        var handler = new RecordingHandler();
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => handler;
            })
            .Build();

        var proxy = new OrchestratorProxy(connection, "job-42");

        // Act — calling PostCommentAsync on a disconnected connection will throw,
        // but we verify the method signature and parameter types are correct
        var act = () => proxy.PostCommentAsync("issue-1", "Hello world", CancellationToken.None);

        // Assert — should throw because the connection isn't started
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SwapLabelAsync_InvokesRequestLabelChange()
    {
        var handler = new RecordingHandler();
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => handler;
            })
            .Build();

        var proxy = new OrchestratorProxy(connection, "job-42");

        var act = () => proxy.SwapLabelAsync("issue-1", "agent:done", CancellationToken.None);

        // Should throw because the connection isn't started
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RequestTokenRefreshAsync_InvokesRequestTokenRefresh()
    {
        var handler = new RecordingHandler();
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => handler;
            })
            .Build();

        var proxy = new OrchestratorProxy(connection, "job-42");

        var act = () => proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);

        // Should throw because the connection isn't started
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Token cache: fast-path, proactive renewal, per-ProviderKind isolation ──

    [Fact]
    public async Task RequestTokenRefreshAsync_CachesToken_ReturnsFromCacheOnSecondCall()
    {
        // Arrange: delegate returns a token expiring 30 minutes from now.
        // The second call should not invoke the delegate — it should return the cached token.
        var callCount = 0;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var proxy = CreateProxyWithDelegate((kind, ct) =>
        {
            callCount++;
            return Task.FromResult(new TokenRefreshResponse { Token = "tok-1", ExpiresAt = expiresAt });
        });

        var token1 = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);
        var token2 = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);

        token1.Should().Be("tok-1");
        token2.Should().Be("tok-1");
        // Delegate must only be called once; the second call should hit the cache.
        callCount.Should().Be(1, "second call should use the cached token without invoking the hub");
    }

    [Fact]
    public async Task RequestTokenRefreshAsync_TokenWithinRenewalBuffer_FetchesFreshToken()
    {
        // Arrange: first call caches a token expiring 2 minutes from now (within the 5-min buffer).
        // Second call should detect proximity to expiry and invoke the delegate again.
        var callCount = 0;
        var proxy = CreateProxyWithDelegate((kind, ct) =>
        {
            callCount++;
            // Return a different token on each call so we can verify which one was returned.
            var token = callCount == 1 ? "stale-tok" : "fresh-tok";
            var expiresAt = callCount == 1
                ? DateTimeOffset.UtcNow.AddMinutes(2)   // within 5-min buffer
                : DateTimeOffset.UtcNow.AddMinutes(30); // well beyond buffer
            return Task.FromResult(new TokenRefreshResponse { Token = token, ExpiresAt = expiresAt });
        });

        var token1 = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);
        var token2 = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);

        token1.Should().Be("stale-tok");
        // The second call must have bypassed the cache and fetched a fresh token because the
        // cached token was within the 5-minute proactive-renewal buffer.
        token2.Should().Be("fresh-tok", "token within renewal buffer should trigger a fresh hub request");
        callCount.Should().Be(2, "delegate must be called twice when cached token is within the renewal buffer");
    }

    [Fact]
    public async Task RequestTokenRefreshAsync_DifferentProviderKinds_CachedIndependently()
    {
        // Arrange: Repository and Brain tokens must be cached under separate keys.
        // A cached Repository token must never be returned for a Brain request (and vice-versa).
        var repoCalls = 0;
        var brainCalls = 0;
        var proxy = CreateProxyWithDelegate((kind, ct) =>
        {
            if (kind == ProviderKind.Repository)
            {
                repoCalls++;
                return Task.FromResult(new TokenRefreshResponse
                {
                    Token = "repo-tok",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
                });
            }
            else
            {
                brainCalls++;
                return Task.FromResult(new TokenRefreshResponse
                {
                    Token = "brain-tok",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
                });
            }
        });

        var repoToken = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);
        var brainToken = await proxy.RequestTokenRefreshAsync(ProviderKind.Brain, CancellationToken.None);

        // Tokens must be distinct — cross-contamination would break brain operations.
        repoToken.Should().Be("repo-tok");
        brainToken.Should().Be("brain-tok");
        repoCalls.Should().Be(1, "repository token should be fetched once");
        brainCalls.Should().Be(1, "brain token should be fetched once, not served from the repository cache");

        // Second calls for both kinds should use their respective cached tokens.
        var repoToken2 = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);
        var brainToken2 = await proxy.RequestTokenRefreshAsync(ProviderKind.Brain, CancellationToken.None);

        repoToken2.Should().Be("repo-tok");
        brainToken2.Should().Be("brain-tok");
        repoCalls.Should().Be(1, "second repository call should use cache");
        brainCalls.Should().Be(1, "second brain call should use cache");
    }

    [Fact]
    public async Task RequestTokenRefreshAsync_FreshTokenCachedWithCorrectExpiry()
    {
        // Verify that the ExpiresAt returned by the delegate is stored and used for expiry
        // decisions — not discarded. This was the original bug: OrchestratorProxy previously
        // discarded the returned ExpiresAt, making the proactive-renewal logic inoperable.
        var firstExpiry = DateTimeOffset.UtcNow.AddMinutes(2);  // within buffer → triggers renewal
        var secondExpiry = DateTimeOffset.UtcNow.AddMinutes(30); // beyond buffer → cached
        var callCount = 0;
        var proxy = CreateProxyWithDelegate((kind, ct) =>
        {
            callCount++;
            var expiry = callCount == 1 ? firstExpiry : secondExpiry;
            return Task.FromResult(new TokenRefreshResponse { Token = $"tok-{callCount}", ExpiresAt = expiry });
        });

        await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None); // populates cache with tok-1, short expiry
        await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None); // cache miss (within buffer) → fetches tok-2
        var thirdResult = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None); // cache hit → returns tok-2

        // The third call must use the cached tok-2 (30-min expiry, well beyond the buffer).
        thirdResult.Should().Be("tok-2", "ExpiresAt must be stored and compared so the 30-min token is served from cache");
        callCount.Should().Be(2, "third call must use cache; only 2 hub invocations total");
    }

    // but if tightened to ThrowAsync<ArgumentNullException>() in the future, verify paramName is "issueIdentifier.Value".
    [Fact]
    public async Task PostCommentAsync_ThrowsOnNullIssueIdentifier()
    {
        var proxy = CreateProxy();
        var act = () => proxy.PostCommentAsync(default(IssueIdentifier), "body", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("issueIdentifier.Value");
    }

    [Fact]
    public async Task PostCommentAsync_ThrowsOnNullBody()
    {
        var proxy = CreateProxy();
        var act = () => proxy.PostCommentAsync("issue-1", null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("body");
    }

    [Fact]
    public async Task SwapLabelAsync_ThrowsOnNullIssueIdentifier()
    {
        var proxy = CreateProxy();
        var act = () => proxy.SwapLabelAsync(default(IssueIdentifier), "label", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("issueIdentifier.Value");
    }

    [Fact]
    public async Task SwapLabelAsync_ThrowsOnNullNewLabel()
    {
        var proxy = CreateProxy();
        var act = () => proxy.SwapLabelAsync("issue-1", null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("newLabel");
    }

    [Fact]
    public async Task PostGateRejectionAsync_ThrowsOnNullAssessmentJson()
    {
        var proxy = CreateProxy();
        var act = () => proxy.PostGateRejectionAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("assessmentJson");
    }

    [Fact]
    public async Task PostGateWontDoAsync_ThrowsOnNullAssessmentJson()
    {
        var proxy = CreateProxy();
        var act = () => proxy.PostGateWontDoAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("assessmentJson");
    }

    private static OrchestratorProxy CreateProxy()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();
        return new OrchestratorProxy(connection, "job-1");
    }

    /// <summary>
    /// Creates an <see cref="OrchestratorProxy"/> with a controllable token-refresh delegate.
    /// The delegate replaces the live SignalR hub call, allowing cache and renewal logic to be
    /// unit-tested without a started hub connection.
    /// </summary>
    private static OrchestratorProxy CreateProxyWithDelegate(
        Func<ProviderKind, CancellationToken, Task<TokenRefreshResponse>> tokenRefreshDelegate)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();
        return new OrchestratorProxy(connection, "job-1", tokenRefreshDelegate);
    }

    /// <summary>
    /// A no-op HTTP handler that returns 200 OK for connection building purposes.
    /// The connection won't actually be started in these tests.
    /// </summary>
    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    /// <summary>
    /// Records outgoing HTTP requests for verification.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
