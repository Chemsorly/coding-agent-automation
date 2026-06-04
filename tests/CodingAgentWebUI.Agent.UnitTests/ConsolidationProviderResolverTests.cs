using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
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
}
