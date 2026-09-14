using AwesomeAssertions;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Regression tests for OperationCanceledException laundering in
/// <see cref="TokenVendingService.PrepareAgentConfigsAsync"/>.
/// Fix: critical-provider catch and non-critical catch must both carry
/// <c>when (ex is not OperationCanceledException)</c> so that a cancelled
/// HTTP call surfaces as OCE rather than as InvalidOperationException with
/// "Aborting dispatch".
/// </summary>
public class TokenVendingServiceCancellationTests
{
    private readonly Mock<ILogger> _mockLogger = new();

    // ── Shared helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Generates a valid RSA private key in base64-PEM format suitable for passing
    /// JWT generation without a config-validation failure.
    /// </summary>
    private static string GenerateValidPrivateKeyBase64()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pem));
    }

    /// <summary>
    /// Returns an <see cref="HttpClient"/> backed by a handler that throws
    /// <see cref="OperationCanceledException"/> on every request, simulating a
    /// caller-cancelled HTTP call.
    /// </summary>
    private static HttpClient MakeCancellingHttpClient()
    {
        return new HttpClient(new CancellingHandler());
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            // TODO [WARNING]: The conditional `cancellationToken.IsCancellationRequested ? cancellationToken
            // : new CancellationToken(canceled: true)` is dead code in the current test context — all tests
            // pass CancellationToken.None, so IsCancellationRequested is always false and a fabricated
            // already-cancelled token is always used. This means the resulting OCE's CancellationToken
            // does not match the caller's token (which is fine — tests only check exception type). Consider
            // simplifying to always return `Task.FromCanceled<HttpResponseMessage>(new CancellationToken(true))`
            // to avoid misleading readers into thinking the handler respects the incoming token. (TestQualityReviewer warning)
            => Task.FromCanceled<HttpResponseMessage>(
                cancellationToken.IsCancellationRequested
                    ? cancellationToken
                    : new CancellationToken(canceled: true));
    }

    private static ProviderConfig MakeGitHubConfig(string id) => new()
    {
        Id = id,
        Kind = ProviderKind.Repository,
        ProviderType = "GitHub",
        DisplayName = $"Repo {id}",
        Settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.PrivateKeyBase64] = GenerateValidPrivateKeyBase64(),
            [ProviderSettingKeys.ClientId] = "Iv1.abc123",
            [ProviderSettingKeys.InstallationId] = "12345678",
            [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
            [ProviderSettingKeys.Owner] = "test-owner",
            [ProviderSettingKeys.Repo] = "test-repo",
        }
    };

    // ── Fix A: critical-provider OCE must not be laundered ──────────────

    /// <summary>
    /// Regression test for the "Aborting dispatch" laundering bug.
    /// When the HTTP call to GitHub is cancelled (caller timeout / request abort),
    /// the critical-provider catch must NOT wrap the OCE in InvalidOperationException.
    /// The OCE must propagate unchanged so EnrichAsync's `when (ex is not OCE)` guard
    /// can suppress the Error log and correctly classify the event as a client abort.
    /// </summary>
    [Fact]
    public async Task PrepareAgentConfigsAsync_CriticalProvider_CancellationRethrowsAsOperationCanceledException()
    {
        // ARRANGE
        var service = new TokenVendingService(_mockLogger.Object, MakeCancellingHttpClient());
        var criticalConfig = MakeGitHubConfig("repo-critical");
        var configs = new List<ProviderConfig> { criticalConfig }.AsReadOnly();

        // ACT — config.Id == repoConfigId → critical-provider branch
        var act = () => service.PrepareAgentConfigsAsync(configs, "repo-critical", CancellationToken.None);

        // ASSERT — must surface as OperationCanceledException, NOT InvalidOperationException
        await act.Should().ThrowAsync<OperationCanceledException>(
            "a cancelled HTTP call must not be laundered into InvalidOperationException('Aborting dispatch')");
    }

    /// <summary>
    /// Verifies that the critical-provider OCE is never wrapped in InvalidOperationException —
    /// i.e., the re-throw must preserve the original OCE type, not produce a wrapping exception.
    /// </summary>
    [Fact]
    public async Task PrepareAgentConfigsAsync_CriticalProvider_CancellationDoesNotThrowInvalidOperationException()
    {
        var service = new TokenVendingService(_mockLogger.Object, MakeCancellingHttpClient());
        var criticalConfig = MakeGitHubConfig("repo-critical");
        var configs = new List<ProviderConfig> { criticalConfig }.AsReadOnly();

        var act = () => service.PrepareAgentConfigsAsync(configs, "repo-critical", CancellationToken.None);

        // InvalidOperationException must NOT be thrown
        await act.Should().NotThrowAsync<InvalidOperationException>(
            "wrapping OCE in InvalidOperationException is the laundering bug — this must not happen");
    }

    // ── Fix A: non-critical-provider OCE must propagate, not degrade ────

    /// <summary>
    /// When a non-critical provider (brain, pipeline) token generation is cancelled,
    /// the OCE must propagate rather than being swallowed as graceful degradation.
    /// Cancellation means the entire request is torn down — partial results are incorrect.
    /// </summary>
    [Fact]
    public async Task PrepareAgentConfigsAsync_NonCriticalProvider_CancellationPropagates()
    {
        // ARRANGE: config.Id != repoConfigId → non-critical branch
        var service = new TokenVendingService(_mockLogger.Object, MakeCancellingHttpClient());
        var nonCriticalConfig = MakeGitHubConfig("repo-brain");
        var configs = new List<ProviderConfig> { nonCriticalConfig }.AsReadOnly();

        var act = () => service.PrepareAgentConfigsAsync(configs, "repo-work", CancellationToken.None);

        // ASSERT — OCE propagates (not swallowed, not wrapped)
        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation of a non-critical provider must propagate: a cancelled request must not yield a partial result");
    }

    /// <summary>
    /// Verifies no partial result list is returned when a non-critical provider is cancelled.
    /// The method must throw, not return a list missing the cancelled provider.
    /// </summary>
    [Fact]
    public async Task PrepareAgentConfigsAsync_NonCriticalProvider_CancellationDoesNotReturnPartialResult()
    {
        var service = new TokenVendingService(_mockLogger.Object, MakeCancellingHttpClient());

        // Mix: a non-GitHub config (passes through without HTTP) followed by a GitHub non-critical config
        var passthroughConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Agent",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ExecutablePath] = "/usr/bin/kiro-cli",
            }
        };
        var nonCriticalGitHub = MakeGitHubConfig("repo-brain");

        var configs = new List<ProviderConfig> { passthroughConfig, nonCriticalGitHub }.AsReadOnly();

        // ACT — non-critical GitHub config will cancel; method must throw, not return [passthroughConfig]
        var act = () => service.PrepareAgentConfigsAsync(configs, "repo-work", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the method must throw OCE rather than return a partial list when cancellation occurs mid-loop");
    }
}

// TODO [WARNING]: The tests in this file exercise the OCE path via a CancellingHandler that throws
// OCE from inside SendAsync (after the semaphore in GenerateAgentTokenAsync is acquired and released
// cleanly). None of them exercise the pre-cancelled-token path where `ct` is already cancelled
// *before* `sem.WaitAsync(ct)` is reached in GenerateAgentTokenAsync. In that scenario,
// `sem.WaitAsync(ct)` throws OCE without acquiring the semaphore; if sem.Release() were incorrectly
// placed inside the try block (i.e., without the `acquired` guard described in the .NET Specialist
// warning in TokenVendingService.cs), a SemaphoreFullException would be thrown instead, defeating
// Fix A. Add a test that passes an already-cancelled CancellationToken to GenerateAgentTokenAsync
// (or PrepareAgentConfigsAsync) and asserts the result is OperationCanceledException, not
// SemaphoreFullException. (Correctness [MAJOR] finding #1 / [INFO] finding #14)
