using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
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

    [Fact]
    public void Constructor_NullOrchestrator_ThrowsArgumentNullException()
    {
        var act = () => new LocalPipelineExecutor(
            null!, _mockHttpClientFactory.Object, _defaultConfig, _mockQualityGateValidator.Object, _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("orchestrator");
    }

    [Fact]
    public void Constructor_NullHttpClientFactory_ThrowsArgumentNullException()
    {
        var act = () => new LocalPipelineExecutor(
            _mockOrchestrator.Object, null!, _defaultConfig, _mockQualityGateValidator.Object, _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("httpClientFactory");
    }

    [Fact]
    public void Constructor_NullDefaultPipelineConfig_ThrowsArgumentNullException()
    {
        var act = () => new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, null!, _mockQualityGateValidator.Object, _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("defaultPipelineConfig");
    }

    [Fact]
    public void Constructor_NullQualityGateValidator_ThrowsArgumentNullException()
    {
        var act = () => new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig, null!, _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("qualityGateValidator");
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig, _mockQualityGateValidator.Object, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void Constructor_ValidParameters_DoesNotThrow()
    {
        var act = () => new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig, _mockQualityGateValidator.Object, _mockLogger.Object);

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_NullBrainUpdateService_DoesNotThrow()
    {
        var act = () => new LocalPipelineExecutor(
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig, _mockQualityGateValidator.Object, _mockLogger.Object, null);

        act.Should().NotThrow();
    }

    // ── WriteMcpConfigToWorkspace ────────────────────────────────────────

    [Fact]
    public void WriteMcpConfigToWorkspace_ValidStdioServers_ProducesValidJson()
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
        var relativePath = ".kiro/settings/mcp.json";

        // Act
        LocalPipelineExecutor.WriteMcpConfigToWorkspace(_tempDir, servers, relativePath);

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
    public void WriteMcpConfigToWorkspace_EmptyServerList_ProducesEmptyMcpServersObject()
    {
        // Arrange
        var servers = new List<McpServerConfig>();
        var relativePath = "mcp-config/mcp.json";

        // Act
        LocalPipelineExecutor.WriteMcpConfigToWorkspace(_tempDir, servers, relativePath);

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
    public void WriteMcpConfigToWorkspace_BothStdioAndHttpServers_SerializesCorrectly()
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
        var relativePath = ".kiro/settings/mcp.json";

        // Act
        LocalPipelineExecutor.WriteMcpConfigToWorkspace(_tempDir, servers, relativePath);

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
    public void WriteMcpConfigToWorkspace_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var servers = new List<McpServerConfig>
        {
            new() { Name = "test-server", Type = "stdio", Command = "node" }
        };
        var relativePath = "nested/deep/path/mcp.json";

        // Act
        LocalPipelineExecutor.WriteMcpConfigToWorkspace(_tempDir, servers, relativePath);

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
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig, _mockQualityGateValidator.Object, _mockLogger.Object);

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
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig, _mockQualityGateValidator.Object, _mockLogger.Object);
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
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig, _mockQualityGateValidator.Object, _mockLogger.Object);

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
            _mockOrchestrator.Object, _mockHttpClientFactory.Object, _defaultConfig, _mockQualityGateValidator.Object, _mockLogger.Object);

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

    // ── Helpers ──────────────────────────────────────────────────────────

    private static HubConnection CreateDisconnectedHubConnection()
    {
        return new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/agent", options =>
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

    /// <summary>
    /// A no-op HTTP handler for building disconnected HubConnections.
    /// </summary>
    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
