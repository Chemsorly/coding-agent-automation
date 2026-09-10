using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="SignalRAgentCommunication"/>.
/// </summary>
public class SignalRAgentCommunicationTests
{
    private readonly Mock<IHubContext<AgentHub, IAgentHubClient>> _mockHubContext;
    private readonly Mock<IHubClients<IAgentHubClient>> _mockClients;
    private readonly Mock<IAgentHubClient> _mockClient;
    private readonly SignalRAgentCommunication _comm;

    public SignalRAgentCommunicationTests()
    {
        _mockHubContext = new Mock<IHubContext<AgentHub, IAgentHubClient>>();
        _mockClients = new Mock<IHubClients<IAgentHubClient>>();
        _mockClient = new Mock<IAgentHubClient>();

        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
        _mockClients.Setup(c => c.Client(It.IsAny<string>())).Returns(_mockClient.Object);

        _comm = new SignalRAgentCommunication(_mockHubContext.Object);
    }

    [Fact]
    public async Task RequestFetchModelsAsync_DelegatesToHubContext()
    {
        var request = new FetchModelsRequest { RequestId = "req-1" };

        await _comm.RequestFetchModelsAsync("conn-1", request, CancellationToken.None);

        _mockClients.Verify(c => c.Client("conn-1"), Times.Once);
        _mockClient.Verify(c => c.RequestFetchModels(request), Times.Once);
    }

    [Fact]
    public async Task CancelJobAsync_DelegatesToHubContext()
    {
        await _comm.CancelJobAsync("conn-1", "job-1", CancellationToken.None);

        _mockClients.Verify(c => c.Client("conn-1"), Times.Once);
        _mockClient.Verify(c => c.CancelJob("job-1"), Times.Once);
    }

    [Fact]
    public void Constructor_NullHubContext_ThrowsArgumentNullException()
    {
        var act = () => new SignalRAgentCommunication(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task CancelJobAsync_NullConnectionId_ThrowsArgumentNullException()
    {
        var act = () => _comm.CancelJobAsync(null!, "job-1", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CancelJobAsync_NullJobId_ThrowsArgumentNullException()
    {
        var act = () => _comm.CancelJobAsync("conn-1", null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AssignConsolidationJobAsync_DelegatesToHubContext()
    {
        AgentId agentId = "agent-1";
        var job = CreateTestConsolidationJob();

        await _comm.AssignConsolidationJobAsync("conn-1", agentId, job, CancellationToken.None);

        _mockClients.Verify(c => c.Client("conn-1"), Times.Once);
        _mockClient.Verify(c => c.AssignConsolidationJob(agentId, job), Times.Once);
    }

    [Fact]
    public async Task AssignConsolidationJobAsync_NullConnectionId_ThrowsArgumentNullException()
    {
        AgentId agentId = "agent-1";
        var job = CreateTestConsolidationJob();

        var act = () => _comm.AssignConsolidationJobAsync(null!, agentId, job, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AssignConsolidationJobAsync_DefaultAgentId_ThrowsArgumentNullException()
    {
        var job = CreateTestConsolidationJob();

        var act = () => _comm.AssignConsolidationJobAsync("conn-1", default, job, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AssignConsolidationJobAsync_NullJob_ThrowsArgumentNullException()
    {
        AgentId agentId = "agent-1";

        var act = () => _comm.AssignConsolidationJobAsync("conn-1", agentId, null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Null guards for RequestFetchModelsAsync ───────────────────────────

    [Fact]
    public async Task RequestFetchModelsAsync_NullConnectionId_ThrowsArgumentNullException()
    {
        var request = new FetchModelsRequest { RequestId = "req-1" };

        var act = () => _comm.RequestFetchModelsAsync(null!, request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RequestFetchModelsAsync_NullRequest_ThrowsArgumentNullException()
    {
        var act = () => _comm.RequestFetchModelsAsync("conn-1", null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static ConsolidationJobMessage CreateTestConsolidationJob() => new()
    {
        JobId = "consolidation-job-1",
        Type = ConsolidationRunType.BrainConsolidation,
        ProviderConfigs = [],
        PipelineConfiguration = new PipelineConfiguration()
    };
}
