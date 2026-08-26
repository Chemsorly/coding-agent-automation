using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for SignalRAgentCommunication null guard validation.
/// The actual SignalR delegation is covered by integration/E2E tests.
/// These tests verify the null guards fire before any SignalR call is attempted.
/// </summary>
public sealed class SignalRAgentCommunicationTests
{
    private readonly Mock<IHubContext<AgentHub, IAgentHubClient>> _hubContext = new();
    private readonly Mock<IHubClients<IAgentHubClient>> _clients = new();
    private readonly Mock<IAgentHubClient> _client = new();
    private readonly SignalRAgentCommunication _sut;

    public SignalRAgentCommunicationTests()
    {
        _hubContext.Setup(h => h.Clients).Returns(_clients.Object);
        _clients.Setup(c => c.Client(It.IsAny<string>())).Returns(_client.Object);
        _client.Setup(c => c.AssignJob(It.IsAny<JobAssignmentMessage>())).Returns(Task.CompletedTask);
        _client.Setup(c => c.RequestFetchModels(It.IsAny<FetchModelsRequest>())).Returns(Task.CompletedTask);
        _client.Setup(c => c.ForceDisconnect()).Returns(Task.CompletedTask);
        _client.Setup(c => c.CancelJob(It.IsAny<JobId>())).Returns(Task.CompletedTask);
        _client.Setup(c => c.AssignConsolidationJob(It.IsAny<AgentId>(), It.IsAny<ConsolidationJobMessage>())).Returns(Task.CompletedTask);

        _sut = new SignalRAgentCommunication(_hubContext.Object);
    }

    // ── Constructor guard ─────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullHubContext_Throws()
    {
        var act = () => new SignalRAgentCommunication(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── AssignJobAsync null guards ────────────────────────────────────────

    [Fact]
    public async Task AssignJobAsync_NullConnectionId_Throws()
    {
        var act = () => _sut.AssignJobAsync(null!, new JobAssignmentMessage
        {
            JobId = "j1", IssueIdentifier = new IssueIdentifier("GH-1"),
            IssueDetail = new IssueDetail { Identifier = new IssueIdentifier("GH-1"), Title = "", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { AcceptanceCriteria = [], RequirementsSection = "" },
            IssueComments = [], ProviderConfigs = [], QualityGateConfigs = [], McpServers = [],
            ReviewerConfigs = [], InitiatedBy = "t", RepoProviderConfigId = "r", AgentProviderConfigId = "a",
            PipelineConfiguration = new PipelineConfiguration()
        });
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AssignJobAsync_NullJob_Throws()
    {
        var act = () => _sut.AssignJobAsync("conn-1", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AssignJobAsync_ValidArgs_DelegatesToClient()
    {
        var job = new JobAssignmentMessage
        {
            JobId = "j1", IssueIdentifier = new IssueIdentifier("GH-1"),
            IssueDetail = new IssueDetail { Identifier = new IssueIdentifier("GH-1"), Title = "", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { AcceptanceCriteria = [], RequirementsSection = "" },
            IssueComments = [], ProviderConfigs = [], QualityGateConfigs = [], McpServers = [],
            ReviewerConfigs = [], InitiatedBy = "t", RepoProviderConfigId = "r", AgentProviderConfigId = "a",
            PipelineConfiguration = new PipelineConfiguration()
        };

        await _sut.AssignJobAsync("conn-1", job);

        _client.Verify(c => c.AssignJob(job), Times.Once);
    }

    // ── ForceDisconnectAsync null guard ───────────────────────────────────

    [Fact]
    public async Task ForceDisconnectAsync_NullConnectionId_Throws()
    {
        var act = () => _sut.ForceDisconnectAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ForceDisconnectAsync_ValidConnectionId_DelegatesToClient()
    {
        await _sut.ForceDisconnectAsync("conn-1");
        _client.Verify(c => c.ForceDisconnect(), Times.Once);
    }

    // ── CancelJobAsync null guards ────────────────────────────────────────

    [Fact]
    public async Task CancelJobAsync_NullConnectionId_Throws()
    {
        var act = () => _sut.CancelJobAsync(null!, "job-1");
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CancelJobAsync_NullJobId_Throws()
    {
        var act = () => _sut.CancelJobAsync("conn-1", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CancelJobAsync_ValidArgs_DelegatesToClient()
    {
        await _sut.CancelJobAsync("conn-1", "job-1");
        _client.Verify(c => c.CancelJob(It.IsAny<JobId>()), Times.Once);
    }

    // ── RequestFetchModelsAsync null guards ───────────────────────────────

    [Fact]
    public async Task RequestFetchModelsAsync_NullConnectionId_Throws()
    {
        var act = () => _sut.RequestFetchModelsAsync(null!, new FetchModelsRequest { RequestId = "r1" });
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestFetchModelsAsync_NullRequest_Throws()
    {
        var act = () => _sut.RequestFetchModelsAsync("conn-1", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static ConsolidationJobMessage MakeConsolidationMsg() => new()
    {
        JobId = "j1",
        Type = ConsolidationRunType.BrainConsolidation,
        ProviderConfigs = [],
        PipelineConfiguration = new PipelineConfiguration()
    };

    [Fact]
    public async Task AssignConsolidationJobAsync_NullConnectionId_Throws()
    {
        var act = () => _sut.AssignConsolidationJobAsync(null!, new AgentId("a1"), MakeConsolidationMsg());
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AssignConsolidationJobAsync_NullAgentIdValue_Throws()
    {
        var act = () => _sut.AssignConsolidationJobAsync("conn-1", new AgentId(null!), MakeConsolidationMsg());
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AssignConsolidationJobAsync_NullJob_Throws()
    {
        var act = () => _sut.AssignConsolidationJobAsync("conn-1", new AgentId("a1"), null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
