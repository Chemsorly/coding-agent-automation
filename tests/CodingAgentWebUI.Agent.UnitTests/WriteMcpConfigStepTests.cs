using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="WriteMcpConfigStep"/>.
/// Tests ExecuteAsync behavior with zero/non-zero MCP servers, graceful degradation on IOException,
/// and default mcpConfigPath resolution.
/// Requirements: 3.1, 3.2, 3.3, 3.4
/// </summary>
public class WriteMcpConfigStepTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();

    public WriteMcpConfigStepTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── ExecuteAsync with zero MCP servers ───────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ZeroMcpServers_ReturnsContinueWithoutWritingFile()
    {
        // Arrange
        var job = CreateJobWithMcpServers([]);
        var step = new WriteMcpConfigStep(job);
        var context = CreateTestContext(_tempDir);

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        _mockCallbacks.Verify(c => c.EmitOutputLine(It.IsAny<string>()), Times.Never);

        // Verify no file was written
        var mcpConfigPath = Path.Combine(_tempDir, ".agent", "settings", "mcp.json");
        File.Exists(mcpConfigPath).Should().BeFalse();
    }

    // ── ExecuteAsync with MCP servers ────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WithMcpServers_ReturnsContinueAndEmitsOutputLine()
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
        var job = CreateJobWithMcpServers(servers);
        var step = new WriteMcpConfigStep(job);
        var context = CreateTestContext(_tempDir);

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        _mockCallbacks.Verify(
            c => c.EmitOutputLine(It.Is<string>(s => s.Contains("1 server(s)"))),
            Times.Once);

        // Verify file was written
        var mcpConfigPath = Path.Combine(_tempDir, ".agent", "settings", "mcp.json");
        File.Exists(mcpConfigPath).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WithMultipleMcpServers_EmitsCorrectServerCount()
    {
        // Arrange
        var servers = new List<McpServerConfig>
        {
            new() { Name = "server-1", Type = "stdio", Command = "node" },
            new() { Name = "server-2", Type = "http", Url = "https://example.com/mcp" },
            new() { Name = "server-3", Type = "stdio", Command = "python" }
        };
        var job = CreateJobWithMcpServers(servers);
        var step = new WriteMcpConfigStep(job);
        var context = CreateTestContext(_tempDir);

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        _mockCallbacks.Verify(
            c => c.EmitOutputLine(It.Is<string>(s => s.Contains("3 server(s)"))),
            Times.Once);
    }

    // ── Graceful degradation on IOException ──────────────────────────────

    [Fact]
    public async Task ExecuteAsync_IoExceptionDuringWrite_ReturnsContinue()
    {
        // Arrange — use a workspace path that will cause an IOException
        // Create a file where the directory should be, causing IOException when trying to create subdirectory
        var blockingFilePath = Path.Combine(_tempDir, ".agent");
        File.WriteAllText(blockingFilePath, "blocking file");

        var servers = new List<McpServerConfig>
        {
            new() { Name = "test-server", Type = "stdio", Command = "node" }
        };
        var job = CreateJobWithMcpServers(servers);
        var step = new WriteMcpConfigStep(job);
        var context = CreateTestContext(_tempDir);

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — graceful degradation: returns Continue despite IOException
        result.Should().Be(StepResult.Continue);
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Once);
    }

    // ── Default mcpConfigPath ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoExplicitMcpConfigPath_UsesDefaultPath()
    {
        // Arrange — job with no agent provider config (so no mcpConfigPath setting)
        var servers = new List<McpServerConfig>
        {
            new() { Name = "test-server", Type = "stdio", Command = "node" }
        };
        var job = CreateJobWithMcpServers(servers, agentProviderConfigId: "non-existent-config");
        var step = new WriteMcpConfigStep(job);
        var context = CreateTestContext(_tempDir);

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — default path ".agent/settings/mcp.json" is used
        _mockCallbacks.Verify(
            c => c.EmitOutputLine(It.Is<string>(s => s.Contains(".agent/settings/mcp.json"))),
            Times.Once);

        var expectedPath = Path.Combine(_tempDir, ".agent", "settings", "mcp.json");
        File.Exists(expectedPath).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitMcpConfigPath_UsesConfiguredPath()
    {
        // Arrange — job with agent provider config that specifies a custom mcpConfigPath
        var customPath = "custom/path/mcp-config.json";
        var agentConfig = new ProviderConfig
        {
            Id = "agent-config-1",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Agent",
            Settings = new Dictionary<string, string> { ["mcpConfigPath"] = customPath }
        };

        var servers = new List<McpServerConfig>
        {
            new() { Name = "test-server", Type = "stdio", Command = "node" }
        };

        var job = new JobAssignmentMessage
        {
            JobId = "test-job",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-config-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [agentConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            McpServers = servers,
            InitiatedBy = "test-user"
        };

        var step = new WriteMcpConfigStep(job);
        var context = CreateTestContext(_tempDir);

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — custom path is used
        _mockCallbacks.Verify(
            c => c.EmitOutputLine(It.Is<string>(s => s.Contains(customPath))),
            Times.Once);

        var expectedPath = Path.Combine(_tempDir, customPath);
        File.Exists(expectedPath).Should().BeTrue();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private PipelineStepContext CreateTestContext(string workspacePath)
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-config-1",
            RepoProviderConfigId = "repo-config-1",
            WorkspacePath = workspacePath,
            StartedAt = DateTime.UtcNow
        };

        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration(),
            RepoProvider = new Mock<IRepositoryProvider>().Object,
            AgentProvider = new Mock<IAgentProvider>().Object,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = new Mock<IConfigurationStore>().Object,
            Callbacks = _mockCallbacks.Object,
            IssueOps = new Mock<IAgentIssueOperations>().Object,
            AgentExecution = new Mock<IAgentPhaseExecutor>().Object,
            QualityGates = new Mock<IQualityGateExecutor>().Object,
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_mockLogger.Object),
            Logger = _mockLogger.Object
        };
    }

    private static JobAssignmentMessage CreateJobWithMcpServers(
        IReadOnlyList<McpServerConfig> mcpServers,
        string agentProviderConfigId = "agent-1")
    {
        return new JobAssignmentMessage
        {
            JobId = "test-job",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = agentProviderConfigId,
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            McpServers = mcpServers,
            InitiatedBy = "test-user"
        };
    }
}
