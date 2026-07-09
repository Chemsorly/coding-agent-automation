using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// TDD tests for <see cref="IWorkItemLifecycleClient"/> interface extraction (R5).
/// Defines the behavioral contract:
/// - WorkItemHttpClient implements IWorkItemLifecycleClient
/// - WorkItemAgentService depends on IWorkItemLifecycleClient (not concrete)
/// - DI resolves IWorkItemLifecycleClient
/// - Full lifecycle testing possible without HTTP
/// </summary>
public class IWorkItemLifecycleClientTests
{
    // ── Interface compliance ─────────────────────────────────────────────

    [Fact]
    public void WorkItemHttpClient_Implements_IWorkItemLifecycleClient()
    {
        var client = CreateWorkItemHttpClient();
        client.Should().BeAssignableTo<IWorkItemLifecycleClient>();
    }

    // ── WorkItemAgentService depends on interface ─────────────────────────

    [Fact]
    public void SourceCode_WorkItemAgentService_DependsOnIWorkItemLifecycleClient()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemAgentService.cs"));

        sourceCode.Should().Contain("IWorkItemLifecycleClient",
            "WorkItemAgentService must depend on IWorkItemLifecycleClient interface, not concrete WorkItemHttpClient");
    }

    // ── Interface definition ─────────────────────────────────────────────

    [Fact]
    public void IWorkItemLifecycleClient_HasGetAssignmentAsyncMethod()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IWorkItemLifecycleClient.cs"));

        sourceCode.Should().Contain("GetAssignmentAsync",
            "IWorkItemLifecycleClient must define GetAssignmentAsync");
    }

    [Fact]
    public void IWorkItemLifecycleClient_HasPostStatusAsyncMethod()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IWorkItemLifecycleClient.cs"));

        sourceCode.Should().Contain("PostStatusAsync",
            "IWorkItemLifecycleClient must define PostStatusAsync");
    }

    [Fact]
    public void IWorkItemLifecycleClient_GetAssignmentAsync_ReturnsNullableJobAssignmentMessage()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IWorkItemLifecycleClient.cs"));

        sourceCode.Should().Contain("Task<JobAssignmentMessage?>",
            "GetAssignmentAsync must return Task<JobAssignmentMessage?> (null = terminal)");
    }

    [Fact]
    public void IWorkItemLifecycleClient_PostStatusAsync_ReturnsBool()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IWorkItemLifecycleClient.cs"));

        sourceCode.Should().Contain("Task<bool> PostStatusAsync",
            "PostStatusAsync must return Task<bool> (accepted or rejected)");
    }

    // ── Behavioral test: mock lifecycle client ───────────────────────────

    [Fact]
    public async Task MockLifecycleClient_GetAssignmentAsync_ReturnsAssignment()
    {
        var mockClient = new Mock<IWorkItemLifecycleClient>();
        var expectedAssignment = new JobAssignmentMessage
        {
            JobId = "test-job",
            IssueIdentifier = "owner/repo#42",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#42", Title = "Test Issue", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            IssueComments = [],
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration(),
            InitiatedBy = "test",
            QualityGateConfigs = []
        };

        mockClient
            .Setup(x => x.GetAssignmentAsync("work-item-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedAssignment);

        var result = await mockClient.Object.GetAssignmentAsync("work-item-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.JobId.Should().Be("test-job");
        result.IssueIdentifier.Should().Be("owner/repo#42");
    }

    [Fact]
    public async Task MockLifecycleClient_GetAssignmentAsync_ReturnsNull_WhenTerminal()
    {
        var mockClient = new Mock<IWorkItemLifecycleClient>();
        mockClient
            .Setup(x => x.GetAssignmentAsync("terminal-item", It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobAssignmentMessage?)null);

        var result = await mockClient.Object.GetAssignmentAsync("terminal-item", CancellationToken.None);

        result.Should().BeNull("terminal work items return null");
    }

    [Fact]
    public async Task MockLifecycleClient_PostStatusAsync_ReturnsTrue_WhenAccepted()
    {
        var mockClient = new Mock<IWorkItemLifecycleClient>();
        mockClient
            .Setup(x => x.PostStatusAsync("work-item-1", It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var update = new WorkItemStatusUpdate { Status = "Running", AgentId = "agent-1" };
        var result = await mockClient.Object.PostStatusAsync("work-item-1", update, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task MockLifecycleClient_PostStatusAsync_ReturnsFalse_WhenRejected()
    {
        var mockClient = new Mock<IWorkItemLifecycleClient>();
        mockClient
            .Setup(x => x.PostStatusAsync("work-item-1", It.IsAny<WorkItemStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var update = new WorkItemStatusUpdate { Status = "Running", AgentId = "agent-1" };
        var result = await mockClient.Object.PostStatusAsync("work-item-1", update, CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── DI Resolution ────────────────────────────────────────────────────

    [Fact]
    public void DI_CanResolve_IWorkItemLifecycleClient()
    {
        var services = new ServiceCollection();

        Serilog.Log.Logger = new Serilog.LoggerConfiguration().CreateLogger();
        services.AddSingleton(Serilog.Log.Logger);

        // Register via AddHttpClient<WorkItemHttpClient> same as production
        services.AddHttpClient<WorkItemHttpClient>(client =>
        {
            client.BaseAddress = new System.Uri("http://localhost:9999");
        });

        // Register interface mapping
        services.AddSingleton<IWorkItemLifecycleClient>(sp =>
            sp.GetRequiredService<WorkItemHttpClient>());

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IWorkItemLifecycleClient>();

        client.Should().NotBeNull();
        client.Should().BeOfType<WorkItemHttpClient>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static WorkItemHttpClient CreateWorkItemHttpClient()
    {
        var httpClient = new HttpClient { BaseAddress = new System.Uri("http://localhost:9999") };
        return new WorkItemHttpClient(httpClient, Mock.Of<Serilog.ILogger>());
    }

    private static string GetSourceDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find solution root");
    }
}
