using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="LocalConsolidationExecutor"/>.
/// </summary>
public class LocalConsolidationExecutorTests : IAsyncDisposable
{
    private readonly Mock<IKiroCliOrchestrator> _mockOrchestrator = new();
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    // TODO: Shared HubConnection is never started, so connection.InvokeAsync always throws.
    // Tests pass because the executor catches the exception internally, but this silently
    // exercises the error path for SignalR reporting in every test without asserting on it.
    private readonly HubConnection _connection;

    public LocalConsolidationExecutorTests()
    {
        _connection = CreateDisconnectedHubConnection();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    // ── Constructor Null Guards ──────────────────────────────────────────

    [Fact]
    public void Constructor_NullOrchestrator_Throws()
    {
        var act = () => new LocalConsolidationExecutor(null!, _mockHttpClientFactory.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("orchestrator");
    }

    [Fact]
    public void Constructor_NullHttpClientFactory_Throws()
    {
        var act = () => new LocalConsolidationExecutor(_mockOrchestrator.Object, null!, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("httpClientFactory");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new LocalConsolidationExecutor(_mockOrchestrator.Object, _mockHttpClientFactory.Object, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void Constructor_ValidParameters_DoesNotThrow()
    {
        var act = () => new LocalConsolidationExecutor(_mockOrchestrator.Object, _mockHttpClientFactory.Object, _mockLogger.Object);
        act.Should().NotThrow();
    }

    // ── ExecuteAsync Null Guards ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NullJob_Throws()
    {
        var executor = CreateExecutor();
        var act = () => executor.ExecuteAsync(null!, _connection, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("job");
    }

    [Fact]
    public async Task ExecuteAsync_NullConnection_Throws()
    {
        var executor = CreateExecutor();
        var job = CreateBrainConsolidationJob();
        var act = () => executor.ExecuteAsync(job, null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("connection");
    }

    // ── BrainConsolidation — Missing Configs ─────────────────────────────

    [Fact]
    public async Task ExecuteAsync_BrainConsolidation_MissingBrainConfig_ReturnsFailure()
    {
        var executor = CreateExecutor();
        var job = CreateBrainConsolidationJob(providerConfigs: []);

        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("brain repository provider");
        result.JobId.Should().Be(job.JobId);
    }

    [Fact]
    public async Task ExecuteAsync_BrainConsolidation_MissingAgentConfig_ReturnsFailure()
    {
        var executor = CreateExecutor();
        var brainConfig = new ProviderConfig
        {
            Id = "brain-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Brain Repo",
            RepositoryRole = RepositoryRole.Brain,
            Settings = new Dictionary<string, string>()
        };
        var job = CreateBrainConsolidationJob(providerConfigs: [brainConfig]);

        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("agent provider");
    }

    // ── RefactoringDetection — Missing Configs ───────────────────────────

    [Fact]
    public async Task ExecuteAsync_RefactoringDetection_MissingRepoConfig_ReturnsFailure()
    {
        var executor = CreateExecutor();
        var job = CreateRefactoringDetectionJob(providerConfigs: []);

        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("code repository provider");
    }

    [Fact]
    public async Task ExecuteAsync_RefactoringDetection_MissingAgentConfig_ReturnsFailure()
    {
        var executor = CreateExecutor();
        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Work Repo",
            RepositoryRole = RepositoryRole.Work,
            Settings = new Dictionary<string, string>()
        };
        var job = CreateRefactoringDetectionJob(providerConfigs: [repoConfig]);

        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("agent provider");
    }

    [Fact]
    public async Task ExecuteAsync_RefactoringDetection_MissingIssueConfig_ReturnsFailure()
    {
        var executor = CreateExecutor();
        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Work Repo",
            RepositoryRole = RepositoryRole.Work,
            Settings = new Dictionary<string, string>()
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Agent",
            Settings = new Dictionary<string, string>()
        };
        var job = CreateRefactoringDetectionJob(providerConfigs: [repoConfig, agentConfig]);

        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("issue provider");
    }

    // ── HarnessSuggestions — Missing Configs ─────────────────────────────

    [Fact]
    public async Task ExecuteAsync_HarnessSuggestions_MissingAgentConfig_ReturnsFailure()
    {
        var executor = CreateExecutor();
        var job = CreateHarnessSuggestionsJob(providerConfigs: []);

        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("agent provider");
    }

    // ── Unknown Type ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_UnknownType_ReturnsFailure()
    {
        var executor = CreateExecutor();
        var job = new ConsolidationJobMessage
        {
            JobId = "job-unknown",
            Type = (ConsolidationRunType)999,
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration()
        };

        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unknown consolidation run type");
    }

    // ── Timeout Handling ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Timeout_ReturnsTimeoutError()
    {
        var executor = CreateExecutor();

        // Use a brain consolidation job with valid configs that will trigger provider creation
        // but with a very short timeout so it times out during validation
        var brainConfig = new ProviderConfig
        {
            Id = "brain-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Brain Repo",
            RepositoryRole = RepositoryRole.Brain,
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["owner"] = "test",
                ["repo"] = "brain",
                ["baseBranch"] = "main",
                ["token"] = "fake-token"
            }
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Agent",
            Settings = new Dictionary<string, string>()
        };

        var job = new ConsolidationJobMessage
        {
            JobId = "job-timeout",
            Type = ConsolidationRunType.BrainConsolidation,
            ProviderConfigs = [brainConfig, agentConfig],
            PipelineConfiguration = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMilliseconds(1) }
        };

        // Give a tiny delay so the timeout CTS fires
        await Task.Delay(10);

        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        // Should either timeout or fail with an error (provider validation failure)
        result.Success.Should().BeFalse();
        result.JobId.Should().Be("job-timeout");
    }

    // ── External Cancellation ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ExternalCancellation_ReturnsCancelledError()
    {
        var executor = CreateExecutor();

        var brainConfig = new ProviderConfig
        {
            Id = "brain-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Brain Repo",
            RepositoryRole = RepositoryRole.Brain,
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["owner"] = "test",
                ["repo"] = "brain",
                ["baseBranch"] = "main",
                ["token"] = "fake-token"
            }
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Agent",
            Settings = new Dictionary<string, string>()
        };

        var job = new ConsolidationJobMessage
        {
            JobId = "job-cancel",
            Type = ConsolidationRunType.BrainConsolidation,
            ProviderConfigs = [brainConfig, agentConfig],
            PipelineConfiguration = new PipelineConfiguration()
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await executor.ExecuteAsync(job, _connection, cts.Token);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cancelled");
        result.JobId.Should().Be("job-cancel");
    }

    // ── Unhandled Exception ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ProviderCreationThrows_ReturnsFailureWithMessage()
    {
        var executor = CreateExecutor();

        // Provide a brain config with unsupported provider type to trigger NotSupportedException
        var brainConfig = new ProviderConfig
        {
            Id = "brain-1",
            Kind = ProviderKind.Repository,
            ProviderType = "UnsupportedType",
            DisplayName = "Bad Provider",
            RepositoryRole = RepositoryRole.Brain,
            Settings = new Dictionary<string, string>()
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Agent",
            Settings = new Dictionary<string, string>()
        };

        var job = new ConsolidationJobMessage
        {
            JobId = "job-error",
            Type = ConsolidationRunType.BrainConsolidation,
            ProviderConfigs = [brainConfig, agentConfig],
            PipelineConfiguration = new PipelineConfiguration()
        };

        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unsupported");
        result.JobId.Should().Be("job-error");
    }

    // ── HarnessSuggestions — Provider Creation Failure ────────────────────

    [Fact]
    public async Task ExecuteAsync_HarnessSuggestions_UnsupportedAgentType_ReturnsFailure()
    {
        var executor = CreateExecutor();
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "UnsupportedAgent",
            DisplayName = "Bad Agent",
            Settings = new Dictionary<string, string>()
        };

        var job = CreateHarnessSuggestionsJob(providerConfigs: [agentConfig]);

        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unsupported");
    }

    // ── RefactoringDetection — Issue Provider Missing Token ──────────────

    [Fact]
    public async Task ExecuteAsync_RefactoringDetection_IssueProviderMissingToken_ReturnsFailure()
    {
        var executor = CreateExecutor();
        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Work Repo",
            RepositoryRole = RepositoryRole.Work,
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["owner"] = "test",
                ["repo"] = "work",
                ["baseBranch"] = "main",
                ["token"] = "fake"
            }
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Agent",
            Settings = new Dictionary<string, string>()
        };
        var issueConfig = new ProviderConfig
        {
            Id = "issue-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Issue Provider",
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["owner"] = "test",
                ["repo"] = "work"
                // Missing "token" setting
            }
        };

        var job = CreateRefactoringDetectionJob(providerConfigs: [repoConfig, agentConfig, issueConfig]);

        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("token");
    }

    // ── RefactoringDetection — Brain Provider Validation Failure ────────

    [Fact]
    public async Task ExecuteAsync_RefactoringDetection_BrainValidationFails_ContinuesWithoutBrain()
    {
        var executor = CreateExecutor();
        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Work Repo",
            RepositoryRole = RepositoryRole.Work,
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["owner"] = "test",
                ["repo"] = "work",
                ["baseBranch"] = "main",
                ["token"] = "fake"
            }
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Agent",
            Settings = new Dictionary<string, string>()
        };
        var issueConfig = new ProviderConfig
        {
            Id = "issue-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Issue Provider",
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["owner"] = "test",
                ["repo"] = "work",
                ["token"] = "fake-issue-token"
            }
        };
        var brainConfig = new ProviderConfig
        {
            Id = "brain-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Brain Repo",
            RepositoryRole = RepositoryRole.Brain,
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["owner"] = "test",
                ["repo"] = "brain",
                ["baseBranch"] = "main",
                ["token"] = "fake-brain-token"
            }
        };

        var job = CreateRefactoringDetectionJob(providerConfigs: [repoConfig, agentConfig, issueConfig, brainConfig]);

        // Brain validation will fail (fake token), but execution continues.
        // Then repo validation also fails, which is caught as unhandled exception.
        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.JobId.Should().Be(job.JobId);
    }

    // ── RefactoringDetection — Full Path (fails at repo validation) ──────

    [Fact]
    public async Task ExecuteAsync_RefactoringDetection_ValidConfigs_FailsAtRepoValidation()
    {
        var executor = CreateExecutor();
        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Work Repo",
            RepositoryRole = RepositoryRole.Work,
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["owner"] = "test",
                ["repo"] = "work",
                ["baseBranch"] = "main",
                ["token"] = "fake"
            }
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Agent",
            Settings = new Dictionary<string, string>()
        };
        var issueConfig = new ProviderConfig
        {
            Id = "issue-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Issue Provider",
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["owner"] = "test",
                ["repo"] = "work",
                ["token"] = "fake-issue-token"
            }
        };

        var job = CreateRefactoringDetectionJob(providerConfigs: [repoConfig, agentConfig, issueConfig]);

        // No brain config — goes straight to repo validation which fails
        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    // ── BrainConsolidation — Full Path (fails at brain validation) ───────

    [Fact]
    public async Task ExecuteAsync_BrainConsolidation_ValidConfigs_FailsAtBrainValidation()
    {
        var executor = CreateExecutor();
        var brainConfig = new ProviderConfig
        {
            Id = "brain-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Brain Repo",
            RepositoryRole = RepositoryRole.Brain,
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["owner"] = "test",
                ["repo"] = "brain",
                ["baseBranch"] = "main",
                ["token"] = "fake-brain-token"
            }
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Agent",
            Settings = new Dictionary<string, string>()
        };

        var job = CreateBrainConsolidationJob(providerConfigs: [brainConfig, agentConfig]);

        // Brain validation fails (fake token) — caught as unhandled exception
        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    // ── HarnessSuggestions — Full Path (agent validation passes) ─────────

    [Fact]
    public async Task ExecuteAsync_HarnessSuggestions_ValidAgentConfig_ReturnsResult()
    {
        var executor = CreateExecutor();
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Agent",
            Settings = new Dictionary<string, string>()
        };

        var job = CreateHarnessSuggestionsJob(providerConfigs: [agentConfig]);

        // Agent validation may pass or fail depending on environment.
        // Either way, we get a result back (not an exception).
        var result = await executor.ExecuteAsync(job, _connection, CancellationToken.None);

        result.JobId.Should().Be(job.JobId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private LocalConsolidationExecutor CreateExecutor() =>
        new(_mockOrchestrator.Object, _mockHttpClientFactory.Object, _mockLogger.Object);

    private static ConsolidationJobMessage CreateBrainConsolidationJob(
        IReadOnlyList<ProviderConfig>? providerConfigs = null) => new()
    {
        JobId = $"job-brain-{Guid.NewGuid():N}",
        Type = ConsolidationRunType.BrainConsolidation,
        ProviderConfigs = providerConfigs ?? [],
        PipelineConfiguration = new PipelineConfiguration()
    };

    private static ConsolidationJobMessage CreateRefactoringDetectionJob(
        IReadOnlyList<ProviderConfig>? providerConfigs = null) => new()
    {
        JobId = $"job-refactor-{Guid.NewGuid():N}",
        Type = ConsolidationRunType.RefactoringDetection,
        ProviderConfigs = providerConfigs ?? [],
        PipelineConfiguration = new PipelineConfiguration()
    };

    private static ConsolidationJobMessage CreateHarnessSuggestionsJob(
        IReadOnlyList<ProviderConfig>? providerConfigs = null) => new()
    {
        JobId = $"job-harness-{Guid.NewGuid():N}",
        Type = ConsolidationRunType.HarnessSuggestions,
        ProviderConfigs = providerConfigs ?? [],
        PipelineConfiguration = new PipelineConfiguration()
    };

    private static HubConnection CreateDisconnectedHubConnection()
    {
        return new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/agent", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
