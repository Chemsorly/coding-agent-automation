using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="ConsolidationProviderResolver"/>.
/// </summary>
public class ConsolidationProviderResolverTests
{
    private readonly Mock<IKiroCliOrchestrator> _mockOrchestrator = new();
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();

    // ── Constructor Null Guards ──────────────────────────────────────────

    [Fact]
    public void Constructor_NullOrchestrator_Throws()
    {
        var act = () => new ConsolidationProviderResolver(null!, _mockHttpClientFactory.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("orchestrator");
    }

    [Fact]
    public void Constructor_NullHttpClientFactory_Throws()
    {
        var act = () => new ConsolidationProviderResolver(_mockOrchestrator.Object, null!, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("httpClientFactory");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new ConsolidationProviderResolver(_mockOrchestrator.Object, _mockHttpClientFactory.Object, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // ── BrainConsolidation — Missing Configs ─────────────────────────────

    [Fact]
    public async Task ResolveBrainConsolidation_MissingBrainConfig_ReturnsFailure()
    {
        var resolver = CreateResolver();
        var job = CreateJob(ConsolidationRunType.BrainConsolidation, []);

        var result = await resolver.ResolveBrainConsolidationProvidersAsync(job, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Failure!.ErrorMessage.Should().Contain("brain repository provider");
        result.Failure.JobId.Should().Be(job.JobId);
    }

    [Fact]
    public async Task ResolveBrainConsolidation_MissingAgentConfig_ReturnsFailure()
    {
        var resolver = CreateResolver();
        var brainConfig = CreateProviderConfig(ProviderKind.Repository, "GitHub", RepositoryRole.Brain);
        var job = CreateJob(ConsolidationRunType.BrainConsolidation, [brainConfig]);

        var result = await resolver.ResolveBrainConsolidationProvidersAsync(job, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Failure!.ErrorMessage.Should().Contain("agent provider");
    }

    // ── RefactoringDetection — Missing Configs ───────────────────────────

    [Fact]
    public async Task ResolveRefactoring_MissingRepoConfig_ReturnsFailure()
    {
        var resolver = CreateResolver();
        var job = CreateJob(ConsolidationRunType.RefactoringDetection, []);

        var result = await resolver.ResolveRefactoringProvidersAsync(job, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Failure!.ErrorMessage.Should().Contain("code repository provider");
    }

    [Fact]
    public async Task ResolveRefactoring_MissingAgentConfig_ReturnsFailure()
    {
        var resolver = CreateResolver();
        var repoConfig = CreateProviderConfig(ProviderKind.Repository, "GitHub", RepositoryRole.Work);
        var job = CreateJob(ConsolidationRunType.RefactoringDetection, [repoConfig]);

        var result = await resolver.ResolveRefactoringProvidersAsync(job, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Failure!.ErrorMessage.Should().Contain("agent provider");
    }

    [Fact]
    public async Task ResolveRefactoring_MissingIssueConfig_ReturnsFailure()
    {
        var resolver = CreateResolver();
        var repoConfig = CreateProviderConfig(ProviderKind.Repository, "GitHub", RepositoryRole.Work);
        var agentConfig = CreateProviderConfig(ProviderKind.Agent, "KiroCli");
        var job = CreateJob(ConsolidationRunType.RefactoringDetection, [repoConfig, agentConfig]);

        var result = await resolver.ResolveRefactoringProvidersAsync(job, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Failure!.ErrorMessage.Should().Contain("issue provider");
    }

    // ── HarnessSuggestions — Missing Configs ─────────────────────────────

    [Fact]
    public async Task ResolveHarness_MissingAgentConfig_ReturnsFailure()
    {
        var resolver = CreateResolver();
        var job = CreateJob(ConsolidationRunType.HarnessSuggestions, []);

        var result = await resolver.ResolveHarnessProvidersAsync(job, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Failure!.ErrorMessage.Should().Contain("agent provider");
    }

    // ── Refactoring — Issue Provider Missing Token ───────────────────────

    [Fact]
    public async Task ResolveRefactoring_IssueProviderMissingToken_Throws()
    {
        var resolver = CreateResolver();
        var repoConfig = CreateProviderConfig(ProviderKind.Repository, "GitHub", RepositoryRole.Work,
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "work",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake"
            });
        var agentConfig = CreateProviderConfig(ProviderKind.Agent, "KiroCli");
        var issueConfig = CreateProviderConfig(ProviderKind.Issue, "GitHub", settings:
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "work"
                // Missing token
            });
        var job = CreateJob(ConsolidationRunType.RefactoringDetection, [repoConfig, agentConfig, issueConfig]);

        // The resolver throws InvalidOperationException for missing token,
        // which propagates up (caught by LocalConsolidationExecutor's catch-all)
        var act = () => resolver.ResolveRefactoringProvidersAsync(job, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*token*");
    }

    // ── Unsupported Provider Type ────────────────────────────────────────

    [Fact]
    public async Task ResolveBrainConsolidation_UnsupportedProviderType_Throws()
    {
        var resolver = CreateResolver();
        var brainConfig = CreateProviderConfig(ProviderKind.Repository, "UnsupportedType", RepositoryRole.Brain);
        var agentConfig = CreateProviderConfig(ProviderKind.Agent, "KiroCli");
        var job = CreateJob(ConsolidationRunType.BrainConsolidation, [brainConfig, agentConfig]);

        var act = () => resolver.ResolveBrainConsolidationProvidersAsync(job, CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    // ── OrchestratorProxy overloads — backward compat and proxy threading ──

    [Fact]
    public async Task ResolveBrainConsolidation_WithNullProxy_BehavesIdenticallyToNoProxyOverload()
    {
        // Regression: passing null proxy explicitly should behave the same as the zero-proxy
        // overload (both produce a factory without a refresh delegate).
        var resolver = CreateResolver();
        var job = CreateJob(ConsolidationRunType.BrainConsolidation, []);

        var resultNoProxy = await resolver.ResolveBrainConsolidationProvidersAsync(job, CancellationToken.None);
        var resultNullProxy = await resolver.ResolveBrainConsolidationProvidersAsync(job, orchestratorProxy: null, CancellationToken.None);

        // Both overloads should return the same failure (no brain config)
        resultNoProxy.IsSuccess.Should().BeFalse();
        resultNullProxy.IsSuccess.Should().BeFalse();
        resultNullProxy.Failure!.ErrorMessage.Should().Be(resultNoProxy.Failure!.ErrorMessage);
    }

    [Fact]
    public async Task ResolveRefactoring_WithNullProxy_BehavesIdenticallyToNoProxyOverload()
    {
        var resolver = CreateResolver();
        var job = CreateJob(ConsolidationRunType.RefactoringDetection, []);

        var resultNoProxy = await resolver.ResolveRefactoringProvidersAsync(job, CancellationToken.None);
        var resultNullProxy = await resolver.ResolveRefactoringProvidersAsync(job, orchestratorProxy: null, CancellationToken.None);

        resultNoProxy.IsSuccess.Should().BeFalse();
        resultNullProxy.IsSuccess.Should().BeFalse();
        resultNullProxy.Failure!.ErrorMessage.Should().Be(resultNoProxy.Failure!.ErrorMessage);
    }

    [Fact]
    public async Task ResolveHarness_WithNullProxy_BehavesIdenticallyToNoProxyOverload()
    {
        var resolver = CreateResolver();
        var job = CreateJob(ConsolidationRunType.HarnessSuggestions, []);

        var resultNoProxy = await resolver.ResolveHarnessProvidersAsync(job, CancellationToken.None);
        var resultNullProxy = await resolver.ResolveHarnessProvidersAsync(job, orchestratorProxy: null, CancellationToken.None);

        resultNoProxy.IsSuccess.Should().BeFalse();
        resultNullProxy.IsSuccess.Should().BeFalse();
        resultNullProxy.Failure!.ErrorMessage.Should().Be(resultNoProxy.Failure!.ErrorMessage);
    }

    // ── Refactoring — Unsupported Issue Provider Type ────────────────────

    [Fact]
    public async Task ResolveRefactoring_UnsupportedIssueProviderType_Throws()
    {
        var resolver = CreateResolver();
        var repoConfig = CreateProviderConfig(ProviderKind.Repository, "GitHub", RepositoryRole.Work,
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "work",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake"
            });
        var agentConfig = CreateProviderConfig(ProviderKind.Agent, "KiroCli");
        var issueConfig = CreateProviderConfig(ProviderKind.Issue, "UnsupportedType");
        var job = CreateJob(ConsolidationRunType.RefactoringDetection, [repoConfig, agentConfig, issueConfig]);

        var act = () => resolver.ResolveRefactoringProvidersAsync(job, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UnsupportedType*");
    }

    // ── Refactoring — GitLab Issue Provider Validation ───────────────────

    [Fact]
    public async Task ResolveRefactoring_GitLabIssueProvider_MissingAccessToken_Throws()
    {
        var resolver = CreateResolver();
        var repoConfig = CreateProviderConfig(ProviderKind.Repository, "GitHub", RepositoryRole.Work,
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "work",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake"
            });
        var agentConfig = CreateProviderConfig(ProviderKind.Agent, "KiroCli");
        var issueConfig = CreateProviderConfig(ProviderKind.Issue, "GitLab", settings:
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.ProjectId] = "123"
            });
        var job = CreateJob(ConsolidationRunType.RefactoringDetection, [repoConfig, agentConfig, issueConfig]);

        var act = () => resolver.ResolveRefactoringProvidersAsync(job, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*accessToken*");
    }

    [Fact]
    public async Task ResolveRefactoring_GitLabIssueProvider_MissingProjectId_Throws()
    {
        var resolver = CreateResolver();
        var repoConfig = CreateProviderConfig(ProviderKind.Repository, "GitHub", RepositoryRole.Work,
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "work",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake"
            });
        var agentConfig = CreateProviderConfig(ProviderKind.Agent, "KiroCli");
        var issueConfig = CreateProviderConfig(ProviderKind.Issue, "GitLab", settings:
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "glpat-fake"
            });
        var job = CreateJob(ConsolidationRunType.RefactoringDetection, [repoConfig, agentConfig, issueConfig]);

        var act = () => resolver.ResolveRefactoringProvidersAsync(job, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*projectId*");
    }

    [Fact]
    public async Task ResolveRefactoring_GitLabIssueProvider_InvalidProjectId_Throws()
    {
        var resolver = CreateResolver();
        var repoConfig = CreateProviderConfig(ProviderKind.Repository, "GitHub", RepositoryRole.Work,
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "work",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake"
            });
        var agentConfig = CreateProviderConfig(ProviderKind.Agent, "KiroCli");
        var issueConfig = CreateProviderConfig(ProviderKind.Issue, "GitLab", settings:
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "glpat-fake",
                [ProviderSettingKeys.ProjectId] = "not-a-number"
            });
        var job = CreateJob(ConsolidationRunType.RefactoringDetection, [repoConfig, agentConfig, issueConfig]);

        var act = () => resolver.ResolveRefactoringProvidersAsync(job, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*projectId*");
    }

    // ── Refactoring — Proxy-based token refresh (issue #2620) ────────────

    /// <summary>
    /// Regression guard: when an OrchestratorProxy is present, a missing 'token' setting must NOT
    /// cause a construction-time InvalidOperationException. The proxy's refresh delegate is used
    /// instead of a static token, so the token null-check is correctly bypassed.
    /// <para>
    /// This test discriminates the two paths: if the static-token branch were taken (regression),
    /// the absent token would cause <c>InvalidOperationException("…missing 'token'…")</c> at
    /// construction time. The proxy branch bypasses that check, so any exception thrown is a
    /// runtime failure from the disconnected hub, not a configuration error.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ResolveRefactoring_WithProxy_GitHubIssueProvider_NoTokenRequired()
    {
        var resolver = CreateResolver();
        var repoConfig = CreateProviderConfig(ProviderKind.Repository, "GitHub", RepositoryRole.Work,
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "work",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake"
            });
        var agentConfig = CreateProviderConfig(ProviderKind.Agent, "KiroCli");
        // Issue config deliberately omits 'token' — proxy path must not require it.
        var issueConfig = CreateProviderConfig(ProviderKind.Issue, "GitHub", settings:
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "work"
            });
        var job = CreateJob(ConsolidationRunType.RefactoringDetection, [repoConfig, agentConfig, issueConfig]);

        using var proxy = CreateTestProxy();

        var act = () => resolver.ResolveRefactoringProvidersAsync(job, proxy, CancellationToken.None);
        var ex = await Record.ExceptionAsync(act);

        // The run must fail (repoProvider.ValidateAsync throws a runtime error against the
        // disconnected hub), but the failure must NOT be the construction-time token-config error.
        // Specifically: a regression to the static-token path (orchestratorProxy null check
        // mistakenly evaluates to false) would throw InvalidOperationException("missing 'token'")
        // because the config has no token. The proxy path bypasses that check, so any exception
        // here is a runtime error from the hub/network layer.
        ex.Should().NotBeNull("a disconnected hub must cause some runtime failure");
        AssertNotTokenConfigError(ex!, "proxy path must bypass the static-token null-check at construction time");
    }

    /// <summary>
    /// Direct constructor-path test: verifies that when an OrchestratorProxy is present,
    /// <see cref="ConsolidationProviderResolver.CreateGitHubIssueProviderForTest"/> constructs
    /// a <see cref="GitHubIssueProvider"/> that invokes the proxy's token-refresh delegate
    /// (not the static token) during <c>ValidateAsync</c>.
    /// <para>
    /// This replaces the previously weak <c>TokenSettingPresentButIgnored</c> test that could
    /// not distinguish the two construction paths because both succeed at construction time when
    /// a token is present.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CreateGitHubIssueProvider_WithProxyAndTokenPresent_UsesRefreshDelegate()
    {
        // Arrange: issue config that has BOTH a static token AND will be used with a proxy.
        // The proxy path must take precedence and invoke the refresh delegate, not the static token.
        var issueConfig = CreateProviderConfig(ProviderKind.Issue, "GitHub", settings:
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "work",
                [ProviderSettingKeys.Token] = "static-token-should-be-ignored"
            });

        var delegateInvokeCount = 0;
        using var proxy = CreateTestProxyWithSpy(onRefresh: () => Interlocked.Increment(ref delegateInvokeCount));

        // Act: construct via the internal test-visible wrapper (proving the correct constructor
        // path was taken), then call ValidateAsync which triggers GetClientAsync → tokenProvider
        // delegate → proxy.RequestTokenRefreshAsync → our spy.
        // ValidateAsync itself will fail at the GitHub API level (network error against the real
        // GitHub endpoint), but the delegate is invoked before any network I/O.
        var provider = ConsolidationProviderResolver.CreateGitHubIssueProviderForTest(issueConfig, proxy);
        var validateEx = await Record.ExceptionAsync(
            () => provider.ValidateAsync(CancellationToken.None));
        await provider.DisposeAsync();

        // Assert: the refresh delegate must have been invoked (proving the Func<> constructor
        // path was taken and the closure correctly calls proxy.RequestTokenRefreshAsync).
        delegateInvokeCount.Should().BeGreaterThan(0,
            "the proxy token-refresh delegate must be invoked during ValidateAsync when a proxy is present, " +
            "proving the refresh-capable constructor was used rather than the static-token constructor");

        // Also verify it didn't fail with a token-config error (which would mean the static path ran).
        AssertNotTokenConfigError(validateEx, "proxy path must not raise a token-config error even when a token setting is present");
    }

    /// <summary>
    /// Regression guard for the PAT/no-proxy path: when orchestratorProxy is null,
    /// a missing 'token' setting must still throw InvalidOperationException at construction time.
    /// </summary>
    [Fact]
    public async Task ResolveRefactoring_WithoutProxy_GitHubIssueProvider_RequiresToken()
    {
        var resolver = CreateResolver();
        var repoConfig = CreateProviderConfig(ProviderKind.Repository, "GitHub", RepositoryRole.Work,
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "work",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake"
            });
        var agentConfig = CreateProviderConfig(ProviderKind.Agent, "KiroCli");
        var issueConfig = CreateProviderConfig(ProviderKind.Issue, "GitHub", settings:
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "work"
                // Missing token — static path requires it
            });
        var job = CreateJob(ConsolidationRunType.RefactoringDetection, [repoConfig, agentConfig, issueConfig]);

        // Explicit null proxy — PAT/static-token path must enforce token presence.
        var act = () => resolver.ResolveRefactoringProvidersAsync(job, orchestratorProxy: null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*token*");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private ConsolidationProviderResolver CreateResolver() =>
        new(_mockOrchestrator.Object, _mockHttpClientFactory.Object, _mockLogger.Object);

    private static ConsolidationJobMessage CreateJob(
        ConsolidationRunType type,
        IReadOnlyList<ProviderConfig> providerConfigs) => new()
        {
            JobId = $"job-{Guid.NewGuid():N}",
            Type = type,
            ProviderConfigs = providerConfigs,
            PipelineConfiguration = new PipelineConfiguration()
        };

    private static ProviderConfig CreateProviderConfig(
        ProviderKind kind,
        string providerType,
        RepositoryRole? role = null,
        Dictionary<string, string>? settings = null) => new()
        {
            Id = $"{kind}-{Guid.NewGuid():N}",
            Kind = kind,
            ProviderType = providerType,
            DisplayName = $"Test {kind}",
            RepositoryRole = role ?? RepositoryRole.Work,
            Settings = settings ?? new Dictionary<string, string>()
        };

    /// <summary>
    /// Creates an OrchestratorProxy wired to a no-op token-refresh delegate.
    /// Uses the internal test-only constructor so no started HubConnection is needed.
    /// Caller owns the proxy and must dispose it.
    /// </summary>
    private static OrchestratorProxy CreateTestProxy()
        => CreateTestProxyWithSpy(onRefresh: null);

    /// <summary>
    /// Creates an OrchestratorProxy wired to a spy token-refresh delegate.
    /// When <paramref name="onRefresh"/> is non-null it is called each time
    /// <c>RequestTokenRefreshAsync</c> is invoked, allowing tests to verify invocation.
    /// Uses the internal test-only constructor so no started HubConnection is needed.
    /// Caller owns the proxy and must dispose it.
    /// </summary>
    /// <remarks>
    /// If tests in this file start failing after a SignalR dependency update, switch to a
    /// HubConnection stub or a minimal fake negotiate handler that returns a valid JSON negotiate
    /// response.
    /// </remarks>
    private static OrchestratorProxy CreateTestProxyWithSpy(Action? onRefresh)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost:9999/hubs/agent",
                options => options.HttpMessageHandlerFactory = _ => new NoOpHttpHandler())
            .Build();
        return new OrchestratorProxy(
            connection,
            "test-job",
            (_, _, _) =>
            {
                onRefresh?.Invoke();
                return Task.FromResult(new TokenRefreshResponse
                {
                    Token = "test-token",
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
                });
            });
    }

    /// <summary>
    /// Unconditional assertion helper: asserts that <paramref name="ex"/> is NOT an
    /// <see cref="InvalidOperationException"/> whose message mentions "token".
    /// This fires regardless of the actual exception type, preventing the conditional-if
    /// anti-pattern where a non-IOE exception silently bypasses the assertion.
    /// </summary>
    /// <remarks>
    /// <c>Record.ExceptionAsync</c> unwraps <see cref="AggregateException"/> to the first inner
    /// for async delegates, so AggregateException wrapping is unlikely in practice, but consider
    /// adding an inner-exception unwrap check if this helper is reused in synchronous or
    /// task-combinator contexts.
    /// </remarks>
    private static void AssertNotTokenConfigError(Exception? ex, string because)
    {
        // The assertion must hold whether ex is null, a different exception type, or an IOE.
        // We cannot use Should().Match<T>() with 'is' patterns (expression tree restriction),
        // so we check via a manual cast.
        if (ex is InvalidOperationException ioe)
        {
            // Config-validation errors say "missing 'token'" or "missing 'accessToken'".
            // Runtime authentication errors (e.g. "Authentication failed: installation token
            // was rejected") also contain the word "token" but are not config errors.
            // We check the specific "missing 'token'" phrase to avoid false positives from
            // legitimate runtime auth failure messages.
            ioe.Message.Should().NotContain("missing 'token'", because);
            ioe.Message.Should().NotContain("missing 'accessToken'", because);
        }
        // Non-IOE exceptions (e.g. HubException, HttpRequestException from disconnected hub
        // during ValidateAsync) are expected runtime failures — not configuration errors.
        // If no exception was thrown the assertion trivially passes.
    }

    private sealed class NoOpHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
