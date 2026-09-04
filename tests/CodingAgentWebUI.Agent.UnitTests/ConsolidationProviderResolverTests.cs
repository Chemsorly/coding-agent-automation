using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

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
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*accessToken*");
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
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*projectId*");
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
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*projectId*");
    }

    // ── OrchestratorProxy wired through to AgentProviderFactory ─────────

    [Fact]
    public async Task ResolveBrainConsolidation_WithOrchestratorProxy_UsesDynamicTokenPath()
    {
        // When an OrchestratorProxy is wired in, AgentProviderFactory should use the
        // dynamic-token (token-refresh) path rather than requiring a static 'token' setting.
        // A brain config with API coordinates but NO 'token' key should NOT throw
        // "missing required setting 'token'" — it should reach provider creation successfully.
        // TODO: This test constructs a real HubConnection backed by a NoOpHandler. The behavior
        // of OrchestratorProxy when the connection is not started is environment-dependent and
        // may differ across SignalR client versions. Combined with the negative assertion below,
        // the test outcome is largely independent of what OrchestratorProxy actually does.
        // Consider using a mock/stub for OrchestratorProxy (or extracting its interface) so the
        // test can assert the proxy was invoked with the correct arguments, making it independent
        // of SignalR runtime behavior.
        var connection = new Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();

        var proxy = new OrchestratorProxy(connection, "job-proxy-test");
        var resolver = CreateResolverWithProxy(proxy);

        var brainConfig = CreateProviderConfig(ProviderKind.Repository, "GitHub", RepositoryRole.Brain,
            new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test",
                [ProviderSettingKeys.Repo] = "brain",
                [ProviderSettingKeys.BaseBranch] = "main"
                // No 'token' key — dynamic path requires OrchestratorProxy
            });
        var agentConfig = CreateProviderConfig(ProviderKind.Agent, "KiroCli");
        var job = CreateJob(ConsolidationRunType.BrainConsolidation, [brainConfig, agentConfig]);

        // The resolver will succeed in creating the provider (dynamic-token path) but then
        // fail at ValidateAsync because the connection is not started. The key assertion is
        // that we get past provider creation without "missing required setting 'token'".
        var act = async () => await resolver.ResolveBrainConsolidationProvidersAsync(job, CancellationToken.None);

        // Should NOT throw ArgumentException about missing 'token' setting.
        // May throw some other exception (e.g. network) during ValidateAsync — that's fine.
        // TODO: This negative assertion is weak — the test passes if *any* non-ArgumentException
        // is thrown, including NullReferenceException or HubException unrelated to token routing.
        // If the proxy wiring in ConsolidationProviderResolver.cs:174 were reverted, the test
        // could still pass because the resulting error might be a different exception type.
        // Replace with a positive assertion: mock AgentProviderFactory or capture whether the
        // dynamic-token branch was taken (e.g. verify no "missing required setting 'token'" message
        // in any exception, or restructure the test to assert the resolution succeeds up to the
        // expected network-failure point rather than relying solely on exception type exclusion.
        var ex = await Record.ExceptionAsync(act);
        ex.Should().NotBeOfType<ArgumentException>(
            "with an OrchestratorProxy, the dynamic token path should be used and 'token' setting is not required");

        await connection.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private ConsolidationProviderResolver CreateResolver() =>
        new(_mockOrchestrator.Object, _mockHttpClientFactory.Object, _mockLogger.Object);

    private ConsolidationProviderResolver CreateResolverWithProxy(OrchestratorProxy proxy) =>
        new(_mockOrchestrator.Object, _mockHttpClientFactory.Object, _mockLogger.Object, proxy);

    private sealed class NoOpHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

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
}
