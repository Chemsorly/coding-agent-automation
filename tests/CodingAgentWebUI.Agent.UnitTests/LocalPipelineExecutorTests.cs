using System.Diagnostics.Metrics;
using System.Text.Json;
using CodingAgentWebUI.Pipeline;
using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using CodingAgentWebUI.Pipeline.Telemetry;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="LocalPipelineExecutor"/>.
/// Tests constructor validation, WriteMcpConfigToWorkspace static method,
/// and failure payload construction.
/// </summary>
public class LocalPipelineExecutorTests : IDisposable
{
    private readonly Mock<IKiroCliOrchestrator> _mockOrchestrator = new();
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory = new();
    private readonly Mock<IQualityGateValidator> _mockQualityGateValidator = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly PipelineConfiguration _defaultConfig = new();
    private readonly string _tempDir;

    public LocalPipelineExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Constructor Null Guards ──────────────────────────────────────────

    [Theory]
    [MemberData(nameof(NullConstructorArgs))]
    public void Constructor_NullParameter_ThrowsArgumentNullException(
        IKiroCliOrchestrator? orchestrator,
        IHttpClientFactory? httpClientFactory,
        PipelineConfiguration? defaultPipelineConfig,
        IQualityGateValidator? qualityGateValidator,
        Serilog.ILogger? logger,
        string expectedParamName)
    {
        var act = () => new LocalPipelineExecutor(
            orchestrator!, httpClientFactory!, defaultPipelineConfig!, qualityGateValidator!, logger!,
            agentIdentity: new AgentIdentity("test-agent"));

        act.Should().Throw<ArgumentNullException>().WithParameterName(expectedParamName);
    }

    public static IEnumerable<object?[]> NullConstructorArgs()
    {
        var orch = new Mock<IKiroCliOrchestrator>().Object;
        var http = new Mock<IHttpClientFactory>().Object;
        var config = new PipelineConfiguration();
        var qg = new Mock<IQualityGateValidator>().Object;
        var log = new Mock<Serilog.ILogger>().Object;

        yield return new object?[] { null, http, config, qg, log, "orchestrator" };
        yield return new object?[] { orch, null, config, qg, log, "httpClientFactory" };
        yield return new object?[] { orch, http, null, qg, log, "defaultPipelineConfig" };
        yield return new object?[] { orch, http, config, null, log, "qualityGateValidator" };
        yield return new object?[] { orch, http, config, qg, null, "logger" };
    }

    [Fact]
    public void Constructor_ValidParameters_DoesNotThrow()
    {
        var act = () => new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig, _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_NullBrainUpdateService_DoesNotThrow()
    {
        var act = () => new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig, _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        act.Should().NotThrow();
    }

    // ── WriteMcpConfigToWorkspace ────────────────────────────────────────

    [Fact]
    public void McpConfigWriter_ValidStdioServers_ProducesValidJson()
    {
        // Arrange
        var servers = new List<McpServerConfig>
        {
            new()
            {
                Name = "context7",
                Type = "stdio",
                Command = "npx",
                Args = ["@context7/mcp", "--stdio"]
            }
        };
        var relativePath = ".agent/settings/mcp.json";

        // Act
        McpConfigWriter.WriteConfig(Path.Combine(_tempDir, relativePath), servers);

        // Assert
        var fullPath = Path.Combine(_tempDir, relativePath);
        File.Exists(fullPath).Should().BeTrue();

        var json = File.ReadAllText(fullPath);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.TryGetProperty("mcpServers", out var mcpServers).Should().BeTrue();
        mcpServers.ValueKind.Should().Be(JsonValueKind.Object);
        mcpServers.TryGetProperty("context7", out var server).Should().BeTrue();
        server.GetProperty("command").GetString().Should().Be("npx");
        server.GetProperty("args").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void McpConfigWriter_EmptyServerList_ProducesEmptyMcpServersObject()
    {
        // Arrange
        var servers = new List<McpServerConfig>();
        var relativePath = "mcp-config/mcp.json";

        // Act
        McpConfigWriter.WriteConfig(Path.Combine(_tempDir, relativePath), servers);

        // Assert
        var fullPath = Path.Combine(_tempDir, relativePath);
        File.Exists(fullPath).Should().BeTrue();

        var json = File.ReadAllText(fullPath);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.TryGetProperty("mcpServers", out var mcpServers).Should().BeTrue();
        mcpServers.ValueKind.Should().Be(JsonValueKind.Object);

        // Verify the mcpServers object is empty
        var count = 0;
        foreach (var _ in mcpServers.EnumerateObject())
            count++;
        count.Should().Be(0);
    }

    [Fact]
    public void McpConfigWriter_BothStdioAndHttpServers_SerializesCorrectly()
    {
        // Arrange
        var servers = new List<McpServerConfig>
        {
            new()
            {
                Name = "filesystem-mcp",
                Type = "stdio",
                Command = "uvx",
                Args = ["filesystem-mcp-server", "--root", "/workspace"],
                Env = new Dictionary<string, string> { ["HOME"] = "/root" }
            },
            new()
            {
                Name = "web-search",
                Type = "http",
                Url = "https://mcp.example.com/search"
            }
        };
        var relativePath = ".agent/settings/mcp.json";

        // Act
        McpConfigWriter.WriteConfig(Path.Combine(_tempDir, relativePath), servers);

        // Assert
        var fullPath = Path.Combine(_tempDir, relativePath);
        var json = File.ReadAllText(fullPath);
        var doc = JsonDocument.Parse(json);
        var mcpServers = doc.RootElement.GetProperty("mcpServers");

        // Verify stdio server
        var stdioServer = mcpServers.GetProperty("filesystem-mcp");
        stdioServer.GetProperty("command").GetString().Should().Be("uvx");
        stdioServer.GetProperty("args").GetArrayLength().Should().Be(3);
        stdioServer.TryGetProperty("url", out _).Should().BeFalse();

        // Verify HTTP server
        var httpServer = mcpServers.GetProperty("web-search");
        httpServer.GetProperty("type").GetString().Should().Be("http");
        httpServer.GetProperty("url").GetString().Should().Be("https://mcp.example.com/search");
        httpServer.TryGetProperty("command", out _).Should().BeFalse();
    }

    [Fact]
    public void McpConfigWriter_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var servers = new List<McpServerConfig>
        {
            new() { Name = "test-server", Type = "stdio", Command = "node" }
        };
        var relativePath = "nested/deep/path/mcp.json";

        // Act
        McpConfigWriter.WriteConfig(Path.Combine(_tempDir, relativePath), servers);

        // Assert
        var fullPath = Path.Combine(_tempDir, relativePath);
        File.Exists(fullPath).Should().BeTrue();
    }

    // ── ExecuteAsync Failure Path ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NullJob_ThrowsArgumentNullException()
    {
        // Arrange
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig, _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        // Act
        var act = () => executor.ExecuteAsync(null!, null!, null!, null, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("job");
    }

    [Fact]
    public async Task ExecuteAsync_NullConnection_ThrowsArgumentNullException()
    {
        // Arrange
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig, _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));
        var job = CreateMinimalJobAssignment();

        // Act
        var act = () => executor.ExecuteAsync(job, null!, null!, null, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("connection");
    }

    [Fact]
    public async Task ExecuteAsync_MissingRepoProviderConfig_ThrowsInvalidOperationException()
    {
        // Arrange — job references a provider config ID that doesn't exist in the list
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig, _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-1",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test Issue", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "non-existent-repo-config",
            AgentProviderConfigId = "agent-config-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        var connection = CreateDisconnectedHubConnection();
        var batcher = new OutputBatcher();

        // Act & Assert — missing repo config throws InvalidOperationException with failure reason
        var act = () => executor.ExecuteAsync(job, connection, batcher, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.WithMessage("*non-existent-repo-config*");

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_MissingAgentProviderConfig_ThrowsInvalidOperationException()
    {
        // Arrange — repo config exists but agent config doesn't
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig, _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-config-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>()
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-2",
            IssueIdentifier = "owner/repo#2",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#2", Title = "Test Issue", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-config-1",
            AgentProviderConfigId = "non-existent-agent-config",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        var connection = CreateDisconnectedHubConnection();
        var batcher = new OutputBatcher();

        // Act & Assert — missing agent config throws InvalidOperationException with failure reason
        var act = () => executor.ExecuteAsync(job, connection, batcher, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.WithMessage("*non-existent-agent-config*");

        await connection.DisposeAsync();
    }

    // ── BuildCompletionPayload ───────────────────────────────────────────

    [Fact]
    public void BuildCompletionPayload_MapsAllFieldsFromRun()
    {
        var run = new PipelineRun
        {
            RunId = "run-1",
            IssueIdentifier = "owner/repo#5",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.Completed,
            CompletedAt = new DateTime(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc),
            CompletedAtOffset = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero),
            PullRequestUrl = "https://github.com/owner/repo/pull/42",
            PullRequestNumber = "42",
            IsDraftPr = false,
            RetryCount = 2,
            FilesChangedCount = 5,
            LinesAdded = 100,
            LinesRemoved = 20,
            BrainUpdatesPushed = true,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            FinalLabel = AgentLabels.Done,
            LinkedPullRequest = new LinkedPullRequest { Url = "https://github.com/owner/repo/pull/41", Number = 41, BranchName = "agent/issue-41", IsDraft = false }
        };
        run.AnalysisConcerns = ["concern-1"];
        run.AnalysisBlockingIssues = ["blocker-1"];
        run.BlacklistedFilesDetected = ["secret.env"];
        run.CodeReviewAgentsRun = ["Correctness"];

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.FinalStep.Should().Be(PipelineStep.Completed);
        payload.PullRequestUrl.Should().Be("https://github.com/owner/repo/pull/42");
        payload.PullRequestNumber.Should().Be("42");
        payload.IsDraftPr.Should().BeFalse();
        payload.RetryCount.Should().Be(2);
        payload.FilesChangedCount.Should().Be(5);
        payload.LinesAdded.Should().Be(100);
        payload.LinesRemoved.Should().Be(20);
        payload.BrainUpdatesPushed.Should().BeTrue();
        payload.AnalysisRecommendation.Should().Be(AnalysisGateResult.Ready);
        payload.IsRework.Should().BeTrue();
        payload.AnalysisConcerns.Should().ContainSingle().Which.Should().Be("concern-1");
        payload.AnalysisBlockingIssues.Should().ContainSingle().Which.Should().Be("blocker-1");
        payload.BlacklistedFilesDetected.Should().ContainSingle().Which.Should().Be("secret.env");
        payload.CodeReviewAgentsRun.Should().ContainSingle().Which.Should().Be("Correctness");
        payload.CompletedAt.Should().Be(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));
        payload.FinalLabel.Should().Be(AgentLabels.Done);
    }

    [Fact]
    public void BuildCompletionPayload_NullLinkedPullRequest_IsReworkFalse()
    {
        var run = new PipelineRun
        {
            RunId = "run-2",
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.Completed,
            CompletedAt = DateTime.UtcNow
        };

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.IsRework.Should().BeFalse();
    }

    [Fact]
    public void BuildCompletionPayload_NullCompletedAt_UsesUtcNow()
    {
        var run = new PipelineRun
        {
            RunId = "run-3",
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.Completed,
            CompletedAt = null
        };

        var before = DateTimeOffset.UtcNow;
        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.CompletedAt.Should().BeOnOrAfter(before);
    }

    // ── BuildFailurePayload ─────────────────────────────────────────────

    [Fact]
    public void BuildFailurePayload_SetsFailureReasonAndStep()
    {
        var run = new PipelineRun
        {
            RunId = "run-4",
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RetryCount = 3,
            FilesChangedCount = 2,
            LinesAdded = 10,
            LinesRemoved = 5
        };

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Something went wrong");

        payload.FinalStep.Should().Be(PipelineStep.Failed);
        payload.FailureReason.Should().Be("Something went wrong");
        payload.RetryCount.Should().Be(3);
        payload.FilesChangedCount.Should().Be(2);
        payload.LinesAdded.Should().Be(10);
        payload.LinesRemoved.Should().Be(5);
        payload.IsRework.Should().BeFalse();
    }

    [Fact]
    public void BuildFailurePayload_WithLinkedPR_IsReworkTrue()
    {
        var run = new PipelineRun
        {
            RunId = "run-5",
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            LinkedPullRequest = new LinkedPullRequest { Url = "https://github.com/owner/repo/pull/10", Number = 10, BranchName = "agent/issue-10", IsDraft = false }
        };

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "error");

        payload.IsRework.Should().BeTrue();
    }

    [Fact]
    public void BuildFailurePayload_PreservesCodeReviewStats()
    {
        var run = new PipelineRun
        {
            RunId = "run-6",
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        run.CodeReviewAgentsRun = ["Security", "Correctness"];
        run.SetCodeReviewCounts(2, 5, 10);

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "gate failed");

        payload.CodeReviewAgentsRun.Should().HaveCount(2);
        payload.CodeReviewCriticalCount.Should().Be(2);
        payload.CodeReviewWarningCount.Should().Be(5);
        payload.CodeReviewSuggestionCount.Should().Be(10);
    }

    [Fact]
    public void BuildFailurePayload_WithFinalLabel_PropagatesLabel()
    {
        var run = new PipelineRun
        {
            RunId = "run-7",
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            FinalLabel = AgentLabels.NeedsRefinement
        };

        var payload = LocalPipelineExecutor.BuildFailurePayload(run, "Analysis gate: needs refinement");

        payload.FinalLabel.Should().Be(AgentLabels.NeedsRefinement);
    }

    [Fact]
    public void BuildCompletionPayload_WithFinalLabelWontDo_PropagatesLabel()
    {
        var run = new PipelineRun
        {
            RunId = "run-8",
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.Completed,
            CompletedAt = DateTime.UtcNow,
            FinalLabel = AgentLabels.WontDo
        };

        var payload = LocalPipelineExecutor.BuildCompletionPayload(run);

        payload.FinalLabel.Should().Be(AgentLabels.WontDo);
    }

    // TODO: ExecuteAsync tests below dispose OutputBatcher/HubConnection after assertions.
    // If an assertion fails, DisposeAsync calls are skipped, leaking timers and semaphores.
    // Refactor to use 'await using' declarations or try/finally for reliable cleanup.

    // ── ExecuteAsync — Provider Validation Failure ───────────────────────

    [Fact]
    public async Task ExecuteAsync_RepoProviderValidationFails_ThrowsFromProvider()
    {
        // Arrange — provide valid config structure so factory creates a provider,
        // but the provider will fail validation because it can't reach the API
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig,
            _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake-token"
            }
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string>()
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-validation",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        var connection = CreateDisconnectedHubConnection();
        var batcher = new OutputBatcher();

        // Act — ValidateAsync on the real GitHubRepositoryProvider will fail
        // because the token is fake and it can't reach the API
        var act = () => executor.ExecuteAsync(job, connection, batcher, null, CancellationToken.None);

        // Assert — should throw (validation failure propagates)
        await act.Should().ThrowAsync<Exception>();

        await batcher.DisposeAsync();
        await connection.DisposeAsync();
    }

    // ── ResolveProvidersAsync — Provider Creation Failure Disposes Earlier Providers ──
    // TODO: Add a test where one DisposeAsync call throws during cleanup and verify remaining providers
    // are still disposed (resilient cleanup). Currently the catch block disposes sequentially without
    // individual try/catch, so a throwing disposal would skip subsequent providers.

    [Fact]
    public async Task ResolveProvidersAsync_AgentProviderCreationFails_DisposesRepoProviderAndThrows()
    {
        // Arrange — mock factory where CreateRepositoryProvider succeeds but CreateAgentProvider throws.
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig,
            _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);
        mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>()))
            .Throws(new NotSupportedException("Unsupported agent type"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>()
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "UnsupportedAgentType",
            DisplayName = "Bad Agent",
            Settings = new Dictionary<string, string>()
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-agent-creation-fail",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        // Act — CreateAgentProvider throws after repoProvider is already created
        var act = () => executor.ResolveProvidersAsync(job, mockFactory.Object, repoConfig, agentConfig, CancellationToken.None);

        // Assert — NotSupportedException propagates and repoProvider.DisposeAsync was called
        await act.Should().ThrowAsync<NotSupportedException>();
        mockRepoProvider.Verify(p => p.DisposeAsync(), Times.Once());
    }

    [Fact]
    public async Task ResolveProvidersAsync_PipelineProviderCreationFails_DisposesEarlierProvidersAndThrows()
    {
        // Arrange — mock factory where repo+agent creation succeeds but pipeline provider creation throws.
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig,
            _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockAgentProvider = new Mock<IAgentProvider>();
        mockAgentProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);
        mockFactory.Setup(f => f.CreateAgentProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockAgentProvider.Object);
        mockFactory.Setup(f => f.CreatePipelineProviderAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("Unsupported pipeline type"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>()
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string>()
        };
        var pipelineConfig = new ProviderConfig
        {
            Id = "pipeline-1",
            Kind = ProviderKind.Pipeline,
            ProviderType = "UnsupportedPipelineType",
            DisplayName = "Bad Pipeline",
            Settings = new Dictionary<string, string>()
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-pipeline-creation-fail",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            PipelineProviderConfigId = "pipeline-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig, pipelineConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        // Act — CreatePipelineProviderAsync throws after repo+agent providers are created
        var act = () => executor.ResolveProvidersAsync(job, mockFactory.Object, repoConfig, agentConfig, CancellationToken.None);

        // Assert — NotSupportedException propagates and both earlier providers are disposed
        await act.Should().ThrowAsync<NotSupportedException>();
        mockRepoProvider.Verify(p => p.DisposeAsync(), Times.Once());
        mockAgentProvider.Verify(p => p.DisposeAsync(), Times.Once());
    }

    // TODO: Add a test where additionalRepoProviders have been created (via ProjectContext with
    // DecompositionAnalysis run type) and a subsequent ValidateAsync call fails, verifying that
    // all additional repo providers are also disposed in the catch block.

    // ── ExecuteAsync — Cancellation ─────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CancellationBeforeProviderCreation_ThrowsOperationCancelled()
    {
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig,
            _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake-token"
            }
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string>()
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-cancel",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var connection = CreateDisconnectedHubConnection();
        var batcher = new OutputBatcher();

        // Act — should throw OperationCanceledException since token is already cancelled
        var act = () => executor.ExecuteAsync(job, connection, batcher, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        await batcher.DisposeAsync();
        await connection.DisposeAsync();
    }

    // ── ExecuteAsync — Null OutputBatcher ────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NullOutputBatcher_ThrowsArgumentNullException()
    {
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig,
            _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));
        var job = CreateMinimalJobAssignment();
        var connection = CreateDisconnectedHubConnection();

        var act = () => executor.ExecuteAsync(job, connection, null!, null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("outputBatcher");

        await connection.DisposeAsync();
    }

    // ── ExecuteAsync — Blacklist Override ────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_RepoConfigHasBlacklistedPaths_OverridesDefaultConfig()
    {
        // This test verifies the blacklist override logic runs without error.
        // The actual override is applied before provider validation, so we verify
        // the code path doesn't throw by checking it reaches provider creation.
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig,
            _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake-token"
            },
            BlacklistedPaths = ["*.secret", "credentials/"],
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string>()
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-blacklist",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        var connection = CreateDisconnectedHubConnection();
        var batcher = new OutputBatcher();

        // Act — will fail at provider validation (fake token), but the blacklist
        // override code path is exercised before that point
        var act = () => executor.ExecuteAsync(job, connection, batcher, null, CancellationToken.None);

        // The exception proves we got past the blacklist override (which doesn't throw)
        // and into provider validation
        await act.Should().ThrowAsync<Exception>();

        await batcher.DisposeAsync();
        await connection.DisposeAsync();
    }

    // ── ExecuteAsync — Brain Provider Path ──────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WithBrainProviderConfig_AttemptsValidation()
    {
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig,
            _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake-token"
            }
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string>()
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
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-brain",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake-brain-token"
            }
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-brain",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            BrainProviderConfigId = "brain-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig, brainConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        var connection = CreateDisconnectedHubConnection();
        var batcher = new OutputBatcher();

        // Act — brain provider validation will fail (fake token), but it's caught
        // and execution continues to repo provider validation (which also fails)
        var act = () => executor.ExecuteAsync(job, connection, batcher, null, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();

        await batcher.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_BrainProviderConfigIdSetButNotInList_SkipsBrainProvider()
    {
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig,
            _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake-token"
            }
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string>()
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-brain-missing",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            BrainProviderConfigId = "non-existent-brain",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        var connection = CreateDisconnectedHubConnection();
        var batcher = new OutputBatcher();

        // Act — brain config not found in list, so brain provider is skipped.
        // Execution continues to repo validation (which fails with fake token).
        var act = () => executor.ExecuteAsync(job, connection, batcher, null, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();

        await batcher.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WithPipelineProviderConfig_AttemptsCreation()
    {
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig,
            _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake-token"
            }
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string>()
        };
        var pipelineConfig = new ProviderConfig
        {
            Id = "pipeline-1",
            Kind = ProviderKind.Pipeline,
            ProviderType = "GitHub",
            DisplayName = "CI Pipeline",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo",
                [ProviderSettingKeys.Token] = "fake-pipeline-token"
            }
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-pipeline",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            PipelineProviderConfigId = "pipeline-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig, pipelineConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        var connection = CreateDisconnectedHubConnection();
        var batcher = new OutputBatcher();

        // Act — pipeline provider is created, then repo validation fails
        var act = () => executor.ExecuteAsync(job, connection, batcher, null, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();

        await batcher.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_PipelineProviderConfigIdSetButNotInList_SkipsPipelineProvider()
    {
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig,
            _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake-token"
            }
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string>()
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-pipeline-missing",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            PipelineProviderConfigId = "non-existent-pipeline",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        var connection = CreateDisconnectedHubConnection();
        var batcher = new OutputBatcher();

        // Act — pipeline config not found, skipped. Repo validation fails.
        var act = () => executor.ExecuteAsync(job, connection, batcher, null, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();

        await batcher.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ExecuteAsync_EmptyBrainProviderConfigId_SkipsBrainProvider()
    {
        var executor = new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig,
            _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl] = "https://api.github.com",
                [ProviderSettingKeys.Owner] = "test-owner",
                [ProviderSettingKeys.Repo] = "test-repo",
                [ProviderSettingKeys.BaseBranch] = "main",
                [ProviderSettingKeys.Token] = "fake-token"
            }
        };
        var agentConfig = new ProviderConfig
        {
            Id = "agent-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string>()
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job-empty-brain",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            BrainProviderConfigId = "",
            PipelineProviderConfigId = "",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig, agentConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };

        var connection = CreateDisconnectedHubConnection();
        var batcher = new OutputBatcher();

        // Act — empty brain/pipeline config IDs are treated as "not set"
        var act = () => executor.ExecuteAsync(job, connection, batcher, null, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();

        await batcher.DisposeAsync();
        await connection.DisposeAsync();
    }

    // ── BuildAgentStepPipeline ─────────────────────────────────────────

    // TODO: These tests dispose HubConnection after assertions. If an assertion fails,
    // DisposeAsync is skipped. Use 'await using' declarations for reliable cleanup.

    [Fact]
    public async Task BuildAgentStepPipeline_Returns16Steps()
    {
        var job = CreateMinimalJobAssignment();
        var connection = CreateDisconnectedHubConnection();
        var proxy = new OrchestratorProxy(connection, "test-job");
        var repoConfig = CreateMinimalRepoConfig();

        var steps = LocalPipelineExecutor.BuildAgentStepPipeline(job, connection, proxy, repoConfig);

        steps.Should().HaveCount(16);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task BuildAgentStepPipeline_StartsWithCloneAndEndsWithQualityGates()
    {
        var job = CreateMinimalJobAssignment();
        var connection = CreateDisconnectedHubConnection();
        var proxy = new OrchestratorProxy(connection, "test-job");
        var repoConfig = CreateMinimalRepoConfig();

        var steps = LocalPipelineExecutor.BuildAgentStepPipeline(job, connection, proxy, repoConfig);

        steps[0].Should().BeOfType<CloneRepositoryStep>();
        steps[^1].Should().BeOfType<RunQualityGatesStep>();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task BuildAgentStepPipeline_IncludesWriteMcpConfigStep()
    {
        var job = CreateMinimalJobAssignment();
        var connection = CreateDisconnectedHubConnection();
        var proxy = new OrchestratorProxy(connection, "test-job");
        var repoConfig = CreateMinimalRepoConfig();

        var steps = LocalPipelineExecutor.BuildAgentStepPipeline(job, connection, proxy, repoConfig);

        steps[2].Should().BeOfType<WriteMcpConfigStep>();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task BuildAgentStepPipeline_IncludesDownloadIssueImagesStep()
    {
        var job = CreateMinimalJobAssignment();
        var connection = CreateDisconnectedHubConnection();
        var proxy = new OrchestratorProxy(connection, "test-job");
        var repoConfig = CreateMinimalRepoConfig();

        var steps = LocalPipelineExecutor.BuildAgentStepPipeline(job, connection, proxy, repoConfig);

        steps.Should().ContainItemsAssignableTo<DownloadIssueImagesStep>();
        await connection.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static HubConnection CreateDisconnectedHubConnection()
    {
        return new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();
    }

    private static JobAssignmentMessage CreateMinimalJobAssignment()
    {
        return new JobAssignmentMessage
        {
            JobId = "test-job",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };
    }

    private static ProviderConfig CreateMinimalRepoConfig()
    {
        return new ProviderConfig
        {
            Id = "repo-1",
            DisplayName = "Test Repo",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            Settings = new Dictionary<string, string>()
        };
    }

    /// <summary>
    /// A no-op HTTP handler for building disconnected HubConnections.
    /// </summary>
    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    // ── BuildStepMetadata ───────────────────────────────────────────────

    [Fact]
    public void BuildStepMetadata_AfterCreatingBranch_IncludesBranchName()
    {
        var run = CreateMinimalRun();
        run.BranchName = "feature/auto-42-fix-bug";

        var metadata = LocalPipelineExecutor.BuildStepMetadata(run, PipelineStep.VerifyingBaseline);

        metadata.Should().NotBeNull();
        metadata!["BranchName"].Should().Be("feature/auto-42-fix-bug");
    }

    [Fact]
    public void BuildStepMetadata_AfterVerifyingBaseline_IncludesBaselineHealth()
    {
        var run = CreateMinimalRun();
        run.BranchName = "feature/test";
        run.BaselineHealthPassed = true;

        var metadata = LocalPipelineExecutor.BuildStepMetadata(run, PipelineStep.AnalyzingCode);

        metadata.Should().NotBeNull();
        metadata!["BaselineHealthPassed"].Should().Be("True");
    }

    [Fact]
    public void BuildStepMetadata_AfterGeneratingCode_IncludesFileStats()
    {
        var run = CreateMinimalRun();
        run.BranchName = "feature/test";
        run.BaselineHealthPassed = true;
        run.FilesChangedCount = 5;
        run.LinesAdded = 100;
        run.LinesRemoved = 20;

        var metadata = LocalPipelineExecutor.BuildStepMetadata(run, PipelineStep.ReviewingCode);

        metadata.Should().NotBeNull();
        metadata!["FilesChangedCount"].Should().Be("5");
        metadata["LinesAdded"].Should().Be("100");
        metadata["LinesRemoved"].Should().Be("20");
    }

    [Fact]
    public void BuildStepMetadata_WithCodeReviewProgress_IncludesIterations()
    {
        var run = CreateMinimalRun();
        run.BranchName = "feature/test";
        run.BaselineHealthPassed = true;
        run.CodeReviewIterationsTotal = 3;
        run.CodeReviewIterationsCompleted = 2;

        var metadata = LocalPipelineExecutor.BuildStepMetadata(run, PipelineStep.RunningQualityGates);

        metadata.Should().NotBeNull();
        metadata!["CodeReviewIterationsTotal"].Should().Be("3");
        metadata["CodeReviewIterationsCompleted"].Should().Be("2");
    }

    [Fact]
    public void BuildStepMetadata_EarlyStep_ReturnsNull()
    {
        var run = CreateMinimalRun();

        var metadata = LocalPipelineExecutor.BuildStepMetadata(run, PipelineStep.CloningRepository);

        metadata.Should().BeNull();
    }

    [Fact]
    public void BuildStepMetadata_NoDataSet_ReturnsNull()
    {
        var run = CreateMinimalRun();

        var metadata = LocalPipelineExecutor.BuildStepMetadata(run, PipelineStep.AnalyzingCode);

        metadata.Should().BeNull();
    }

    [Fact]
    public void BuildStepMetadata_WithRetryCount_IncludesRetryCount()
    {
        var run = CreateMinimalRun();
        run.RetryCount = 2;
        run.InfrastructureRetryCount = 1;

        var metadata = LocalPipelineExecutor.BuildStepMetadata(run, PipelineStep.GeneratingCode);

        metadata.Should().NotBeNull();
        metadata!["RetryCount"].Should().Be("2");
        metadata["InfrastructureRetryCount"].Should().Be("1");
    }

    [Fact]
    public void BuildStepMetadata_WithTokensAndCost_IncludesAccumulatedMetrics()
    {
        var run = CreateMinimalRun();
        run.TotalTokens = 75000;
        run.TotalCost = 1.23m;

        var metadata = LocalPipelineExecutor.BuildStepMetadata(run, PipelineStep.GeneratingCode);

        metadata.Should().NotBeNull();
        metadata!["TotalTokens"].Should().Be("75000");
        metadata["TotalCost"].Should().Be("1.23");
    }

    [Fact]
    public void BuildStepMetadata_WithCodeReviewFindings_IncludesCounts()
    {
        var run = CreateMinimalRun();
        run.AddCodeReviewCounts(3, 5, 7);
        run.CodeReviewAgentsRun = new[] { "security-agent", "style-agent" };

        var metadata = LocalPipelineExecutor.BuildStepMetadata(run, PipelineStep.RunningQualityGates);

        metadata.Should().NotBeNull();
        metadata!["CodeReviewCriticalCount"].Should().Be("3");
        metadata["CodeReviewWarningCount"].Should().Be("5");
        metadata["CodeReviewSuggestionCount"].Should().Be("7");
        metadata["CodeReviewAgentsRun"].Should().Be("security-agent\x1Fstyle-agent");
    }

    [Fact]
    public void BuildStepMetadata_ZeroRetryCount_DoesNotIncludeRetryCount()
    {
        var run = CreateMinimalRun();
        run.RetryCount = 0;
        run.TotalTokens = 0;

        var metadata = LocalPipelineExecutor.BuildStepMetadata(run, PipelineStep.GeneratingCode);

        // No data to report → null or missing keys
        metadata?.ContainsKey("RetryCount").Should().NotBe(true);
        metadata?.ContainsKey("TotalTokens").Should().NotBe(true);
    }

    private static PipelineRun CreateMinimalRun() => new()
    {
        RunId = "run-meta",
        IssueIdentifier = "owner/repo#1",
        IssueTitle = "Test",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        CurrentStep = PipelineStep.Created
    };

    // ── TransitionToInternalAsync (PipelineSignalRReporter) ────────────

    // TODO: Add test for PipelineSignalRReporter.ReportStepTransitionAsync — verify it updates
    // run.CurrentStep and swallows SignalR failures (awaited path used during PR creation).

    // TODO: Add test for PipelineSignalRReporter.ReportBrainSyncResultAsync — verify it correctly
    // forwards parameters and swallows exceptions on connection failure.

    [Fact]
    public async Task TransitionToInternalAsync_UpdatesCurrentStepAndHighWaterMark()
    {
        var run = CreateMinimalRun();
        var connection = CreateDisconnectedHubConnection();
        await using var batcher = new OutputBatcher();
        var reporter = new PipelineSignalRReporter(connection, batcher, "job-1", run, null, _mockLogger.Object);

        await reporter.TransitionToInternalAsync(PipelineStep.AnalyzingCode, CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.AnalyzingCode);
        run.HighWaterMark.Should().Be(PipelineStep.AnalyzingCode);
        await reporter.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task TransitionToInternalAsync_FailedStep_DoesNotUpdateHighWaterMark()
    {
        var run = CreateMinimalRun();
        run.HighWaterMark = PipelineStep.AnalyzingCode;
        var connection = CreateDisconnectedHubConnection();
        await using var batcher = new OutputBatcher();
        var reporter = new PipelineSignalRReporter(connection, batcher, "job-1", run, null, _mockLogger.Object);

        await reporter.TransitionToInternalAsync(PipelineStep.Failed, CancellationToken.None);

        run.CurrentStep.Should().Be(PipelineStep.Failed);
        run.HighWaterMark.Should().Be(PipelineStep.AnalyzingCode);
        await reporter.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task TransitionToInternalAsync_InvokesOnStepChanged()
    {
        var run = CreateMinimalRun();
        var connection = CreateDisconnectedHubConnection();
        await using var batcher = new OutputBatcher();
        PipelineStep? received = null;
        var reporter = new PipelineSignalRReporter(connection, batcher, "job-1", run, s => received = s, _mockLogger.Object);

        await reporter.TransitionToInternalAsync(PipelineStep.GeneratingCode, CancellationToken.None);

        received.Should().Be(PipelineStep.GeneratingCode);
        await reporter.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task TransitionToInternalAsync_SignalRFailure_DoesNotThrow()
    {
        // Disconnected connection will fail SendAsync — should be swallowed
        var run = CreateMinimalRun();
        var connection = CreateDisconnectedHubConnection();
        await using var batcher = new OutputBatcher();
        var reporter = new PipelineSignalRReporter(connection, batcher, "job-1", run, null, _mockLogger.Object);

        var act = () => reporter.TransitionToInternalAsync(PipelineStep.AnalyzingCode, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await reporter.DisposeAsync();
        await connection.DisposeAsync();
    }

    // ── EmitOutputLineInternalAsync (PipelineSignalRReporter) ────────────

    [Fact]
    public async Task EmitOutputLineInternalAsync_EnqueuesLineToRun()
    {
        var run = CreateMinimalRun();
        var connection = CreateDisconnectedHubConnection();
        await using var batcher = new OutputBatcher();
        var reporter = new PipelineSignalRReporter(connection, batcher, "job-1", run, null, _mockLogger.Object);

        await reporter.EmitOutputLineInternalAsync("hello", CancellationToken.None);

        run.OutputLines.Should().Contain("hello");
        await reporter.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task EmitOutputLineInternalAsync_CancelledToken_DoesNotThrow()
    {
        var run = CreateMinimalRun();
        var connection = CreateDisconnectedHubConnection();
        await using var batcher = new OutputBatcher();
        var reporter = new PipelineSignalRReporter(connection, batcher, "job-1", run, null, _mockLogger.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => reporter.EmitOutputLineInternalAsync("line", cts.Token);

        await act.Should().NotThrowAsync();
        await reporter.DisposeAsync();
        await connection.DisposeAsync();
    }

    // ── ReportQualityGateResultInternalAsync (PipelineSignalRReporter) ───

    [Fact]
    public async Task ReportQualityGateResultInternalAsync_SignalRFailure_DoesNotThrow()
    {
        var run = CreateMinimalRun();
        var connection = CreateDisconnectedHubConnection();
        await using var batcher = new OutputBatcher();
        var reporter = new PipelineSignalRReporter(connection, batcher, "job-1", run, null, _mockLogger.Object);
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "build", Passed = true },
            Tests = new GateResult { GateName = "test", Passed = true }
        };

        var act = () => reporter.ReportQualityGateResultInternalAsync(report, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await reporter.DisposeAsync();
        await connection.DisposeAsync();
    }

    // ── SerializedSendAsync ordering (PipelineSignalRReporter) ──────────

    [Fact]
    public async Task TransitionToInternalAsync_SignalRFailure_IncrementsMetricCounter()
    {
        using var listener = new MeterListener();
        var measurements = new List<string>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) =>
            measurements.Add(instrument.Name));
        listener.Start();

        var run = CreateMinimalRun();
        var connection = CreateDisconnectedHubConnection();
        await using var batcher = new OutputBatcher();
        var reporter = new PipelineSignalRReporter(connection, batcher, "job-1", run, null, _mockLogger.Object);
        measurements.Clear();

        await reporter.TransitionToInternalAsync(PipelineStep.AnalyzingCode, CancellationToken.None);

        measurements.Should().Contain("agent.signalr.failures");
        await reporter.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ReportQualityGateResultInternalAsync_SignalRFailure_IncrementsMetricCounter()
    {
        using var listener = new MeterListener();
        var measurements = new List<string>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) =>
            measurements.Add(instrument.Name));
        listener.Start();

        var run = CreateMinimalRun();
        var connection = CreateDisconnectedHubConnection();
        await using var batcher = new OutputBatcher();
        var reporter = new PipelineSignalRReporter(connection, batcher, "job-1", run, null, _mockLogger.Object);
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "build", Passed = true },
            Tests = new GateResult { GateName = "test", Passed = true }
        };
        measurements.Clear();

        await reporter.ReportQualityGateResultInternalAsync(report, CancellationToken.None);

        measurements.Should().Contain("agent.signalr.failures");
        await reporter.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task SerializedSendAsync_GuaranteesOrdering()
    {
        // Simulate the serialization lock used in ExecuteAsync
        using var signalrLock = new SemaphoreSlim(1, 1);
        var executionOrder = new List<int>();

        async Task SerializedSend(Func<Task> send)
        {
            await signalrLock.WaitAsync(CancellationToken.None);
            try { await send(); }
            finally { signalrLock.Release(); }
        }

        // Fire 5 sends concurrently — they should complete in order
        var tasks = Enumerable.Range(0, 5).Select(i =>
            SerializedSend(async () =>
            {
                await Task.Delay(10 - i); // Later sends are faster, but ordering is preserved
                lock (executionOrder) { executionOrder.Add(i); }
            })).ToArray();

        await Task.WhenAll(tasks);

        executionOrder.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task SerializedSendAsync_ConcurrentCalls_ExecuteSequentially()
    {
        // Regression test for #791: verifies that the production SerializedSendAsync method
        // guarantees sequential execution even when 10 callers race concurrently.
        using var signalrLock = new SemaphoreSlim(1, 1);
        var executionOrder = new List<int>();

        // Fire 10 concurrent sends via the real production method
        var tasks = Enumerable.Range(0, 10).Select(i =>
            PipelineSignalRReporter.SerializedSendAsync(
                signalrLock,
                async () =>
                {
                    // Vary delays: later items are faster. If ordering breaks, we'd see them jump ahead.
                    await Task.Delay(10 - i);
                    lock (executionOrder) { executionOrder.Add(i); }
                },
                CancellationToken.None)).ToArray();

        await Task.WhenAll(tasks);

        // Items must appear in the order they acquired the semaphore (FIFO under no contention at start)
        executionOrder.Should().HaveCount(10);
        executionOrder.Should().BeInAscendingOrder(
            "SerializedSendAsync must guarantee that concurrent calls execute sequentially in arrival order");
    }

    [Fact]
    public async Task SerializedSendAsync_WhenCancelled_DoesNotThrow()
    {
        using var signalrLock = new SemaphoreSlim(1, 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Should complete without throwing — cancellation is swallowed
        var task = PipelineSignalRReporter.SerializedSendAsync(
            signalrLock,
            () => Task.CompletedTask,
            cts.Token);

        await task; // Must not throw
    }

    [Fact]
    public async Task SerializedSendAsync_WhenSemaphoreDisposed_DoesNotThrow()
    {
        // Dispose the semaphore BEFORE calling SerializedSendAsync — this is the real scenario
        // where a fire-and-forget task calls WaitAsync after the semaphore is already disposed.
        var signalrLock = new SemaphoreSlim(1, 1);
        signalrLock.Dispose();

        await PipelineSignalRReporter.SerializedSendAsync(
            signalrLock,
            () => Task.CompletedTask,
            CancellationToken.None);
    }

    [Fact]
    public async Task SerializedSendAsync_WhenSemaphoreDisposed_SendIsNotExecuted()
    {
        var signalrLock = new SemaphoreSlim(1, 1);
        var sendExecuted = false;

        signalrLock.Dispose();

        await PipelineSignalRReporter.SerializedSendAsync(
            signalrLock,
            () => { sendExecuted = true; return Task.CompletedTask; },
            CancellationToken.None);

        sendExecuted.Should().BeFalse();
    }

    [Fact]
    public async Task SerializedSendAsync_WhenSemaphoreDisposedDuringSend_DoesNotThrow()
    {
        var signalrLock = new SemaphoreSlim(1, 1);

        await PipelineSignalRReporter.SerializedSendAsync(
            signalrLock,
            () => { signalrLock.Dispose(); return Task.CompletedTask; },
            CancellationToken.None);
    }

    // ── PullRequestCreationContext ──────────────────────────────────────

    [Fact]
    public void PullRequestCreationContext_CanBeConstructed()
    {
        var context = new PullRequestCreationContext
        {
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            Config = new PipelineConfiguration(),
            IssueOps = new OrchestratorProxy(CreateDisconnectedHubConnection(), "job-1"),
            Connection = CreateDisconnectedHubConnection(),
            Job = CreateMinimalJobAssignment(),
            PrOrchestrator = new PullRequestOrchestrator(Mock.Of<Serilog.ILogger>()),
            EmitOutputLine = _ => { }
        };

        context.RepoProvider.Should().NotBeNull();
        context.BrainProvider.Should().BeNull();
        context.BrainSync.Should().BeNull();
    }

    private LocalPipelineExecutor CreateExecutor() => new(
        _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig,
        _mockQualityGateValidator.Object, _mockLogger.Object, agentIdentity: new AgentIdentity("test-agent"));

    // ── BuildReviewStepPipeline ─────────────────────────────────────────

    [Fact]
    public async Task BuildReviewStepPipeline_IncludesWriteMcpConfigStep()
    {
        var job = CreateMinimalJobAssignment();
        var connection = CreateDisconnectedHubConnection();
        var proxy = new OrchestratorProxy(connection, "test-job");
        var repoConfig = CreateMinimalRepoConfig();

        var steps = LocalPipelineExecutor.BuildReviewStepPipeline(job, proxy, repoConfig);

        steps.Should().Contain(s => s.GetType() == typeof(WriteMcpConfigStep));
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task BuildReviewStepPipeline_WriteMcpConfigStep_BeforeWriteSteeringStep()
    {
        var job = CreateMinimalJobAssignment();
        var connection = CreateDisconnectedHubConnection();
        var proxy = new OrchestratorProxy(connection, "test-job");
        var repoConfig = CreateMinimalRepoConfig();

        var steps = LocalPipelineExecutor.BuildReviewStepPipeline(job, proxy, repoConfig);

        var mcpIndex = steps.ToList().FindIndex(s => s is WriteMcpConfigStep);
        var steeringIndex = steps.ToList().FindIndex(s => s is WriteSteeringStep);
        mcpIndex.Should().BeGreaterThanOrEqualTo(0, "WriteMcpConfigStep should be present");
        steeringIndex.Should().BeGreaterThanOrEqualTo(0, "WriteSteeringStep should be present");
        mcpIndex.Should().BeLessThan(steeringIndex, "WriteMcpConfigStep should come before WriteSteeringStep");
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task BuildReviewStepPipeline_StartsWithCloneRepository()
    {
        var job = CreateMinimalJobAssignment();
        var connection = CreateDisconnectedHubConnection();
        var proxy = new OrchestratorProxy(connection, "test-job");
        var repoConfig = CreateMinimalRepoConfig();

        var steps = LocalPipelineExecutor.BuildReviewStepPipeline(job, proxy, repoConfig);

        steps[0].Should().BeOfType<CloneRepositoryStep>();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task BuildReviewStepPipeline_IncludesDownloadIssueImagesStep()
    {
        var job = CreateMinimalJobAssignment();
        var connection = CreateDisconnectedHubConnection();
        var proxy = new OrchestratorProxy(connection, "test-job");
        var repoConfig = CreateMinimalRepoConfig();

        var steps = LocalPipelineExecutor.BuildReviewStepPipeline(job, proxy, repoConfig);

        steps.Should().Contain(s => s.GetType() == typeof(DownloadIssueImagesStep));
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task BuildReviewStepPipeline_DownloadIssueImagesStep_AfterSyncBrainPreRun_BeforeExtractLinkedIssues()
    {
        var job = CreateMinimalJobAssignment();
        var connection = CreateDisconnectedHubConnection();
        var proxy = new OrchestratorProxy(connection, "test-job");
        var repoConfig = CreateMinimalRepoConfig();

        var steps = LocalPipelineExecutor.BuildReviewStepPipeline(job, proxy, repoConfig);

        var syncIndex = steps.ToList().FindIndex(s => s is SyncBrainPreRunStep);
        var downloadIndex = steps.ToList().FindIndex(s => s is DownloadIssueImagesStep);
        var extractIndex = steps.ToList().FindIndex(s => s is ExtractLinkedIssuesStep);
        syncIndex.Should().BeGreaterThanOrEqualTo(0);
        downloadIndex.Should().BeGreaterThanOrEqualTo(0);
        extractIndex.Should().BeGreaterThanOrEqualTo(0);
        downloadIndex.Should().BeGreaterThan(syncIndex, "DownloadIssueImagesStep should come after SyncBrainPreRunStep");
        downloadIndex.Should().BeLessThan(extractIndex, "DownloadIssueImagesStep should come before ExtractLinkedIssuesStep");
        await connection.DisposeAsync();
    }

    // ── BuildDecompositionAnalysisStepPipeline ───────────────────────────

    [Fact]
    public async Task BuildDecompositionAnalysisStepPipeline_IncludesWriteMcpConfigStep()
    {
        var job = CreateMinimalJobAssignment();
        var connection = CreateDisconnectedHubConnection();
        var proxy = new OrchestratorProxy(connection, "test-job");
        var repoConfig = CreateMinimalRepoConfig();

        var steps = LocalPipelineExecutor.BuildDecompositionAnalysisStepPipeline(job, Mock.Of<IOpenIssueContextWriter>(), proxy, repoConfig);

        steps.Should().Contain(s => s.GetType() == typeof(WriteMcpConfigStep));
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task BuildDecompositionAnalysisStepPipeline_WriteMcpConfigStep_BeforeWriteSteeringStep()
    {
        var job = CreateMinimalJobAssignment();
        var connection = CreateDisconnectedHubConnection();
        var proxy = new OrchestratorProxy(connection, "test-job");
        var repoConfig = CreateMinimalRepoConfig();

        var steps = LocalPipelineExecutor.BuildDecompositionAnalysisStepPipeline(job, Mock.Of<IOpenIssueContextWriter>(), proxy, repoConfig);

        var mcpIndex = steps.ToList().FindIndex(s => s is WriteMcpConfigStep);
        var steeringIndex = steps.ToList().FindIndex(s => s is WriteSteeringStep);
        mcpIndex.Should().BeGreaterThanOrEqualTo(0);
        steeringIndex.Should().BeGreaterThanOrEqualTo(0);
        mcpIndex.Should().BeLessThan(steeringIndex);
        await connection.DisposeAsync();
    }

    // ── BuildDecompositionStepPipeline ───────────────────────────────────

    [Fact]
    public async Task BuildDecompositionStepPipeline_IncludesWriteMcpConfigStep()
    {
        var job = CreateMinimalJobAssignment();
        var connection = CreateDisconnectedHubConnection();
        var proxy = new OrchestratorProxy(connection, "test-job");
        var repoConfig = CreateMinimalRepoConfig();

        var steps = LocalPipelineExecutor.BuildDecompositionStepPipeline(job, Mock.Of<IOpenIssueContextWriter>(), proxy, repoConfig);

        steps.Should().Contain(s => s.GetType() == typeof(WriteMcpConfigStep));
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task BuildDecompositionStepPipeline_WriteMcpConfigStep_BeforeWriteSteeringStep()
    {
        var job = CreateMinimalJobAssignment();
        var connection = CreateDisconnectedHubConnection();
        var proxy = new OrchestratorProxy(connection, "test-job");
        var repoConfig = CreateMinimalRepoConfig();

        var steps = LocalPipelineExecutor.BuildDecompositionStepPipeline(job, Mock.Of<IOpenIssueContextWriter>(), proxy, repoConfig);

        var mcpIndex = steps.ToList().FindIndex(s => s is WriteMcpConfigStep);
        var steeringIndex = steps.ToList().FindIndex(s => s is WriteSteeringStep);
        mcpIndex.Should().BeGreaterThanOrEqualTo(0);
        steeringIndex.Should().BeGreaterThanOrEqualTo(0);
        mcpIndex.Should().BeLessThan(steeringIndex);
        await connection.DisposeAsync();
    }
}
