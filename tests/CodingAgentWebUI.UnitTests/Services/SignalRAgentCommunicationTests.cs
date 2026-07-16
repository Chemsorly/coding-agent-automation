using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace CodingAgentWebUI.UnitTests.Services;

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
    public async Task AssignJobAsync_DelegatesToHubContext()
    {
        var job = CreateTestJob();

        await _comm.AssignJobAsync("conn-1", job, CancellationToken.None);

        _mockClients.Verify(c => c.Client("conn-1"), Times.Once);
        _mockClient.Verify(c => c.AssignJob(job), Times.Once);
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
    public async Task ForceDisconnectAsync_DelegatesToHubContext()
    {
        await _comm.ForceDisconnectAsync("conn-1", CancellationToken.None);

        _mockClients.Verify(c => c.Client("conn-1"), Times.Once);
        _mockClient.Verify(c => c.ForceDisconnect(), Times.Once);
    }

    [Fact]
    public async Task CancelJobAsync_DelegatesToHubContext()
    {
        await _comm.CancelJobAsync("conn-1", "job-1", CancellationToken.None);

        _mockClients.Verify(c => c.Client("conn-1"), Times.Once);
        _mockClient.Verify(c => c.CancelJob(new JobId("job-1")), Times.Once);
    }

    [Fact]
    public void Constructor_NullHubContext_ThrowsArgumentNullException()
    {
        var act = () => new SignalRAgentCommunication(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task AssignJobAsync_NullConnectionId_ThrowsArgumentNullException()
    {
        var job = CreateTestJob();

        var act = () => _comm.AssignJobAsync(null!, job, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AssignJobAsync_NullJob_ThrowsArgumentNullException()
    {
        var act = () => _comm.AssignJobAsync("conn-1", null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
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

    private static JobAssignmentMessage CreateTestJob() => new()
    {
        JobId = "job-1",
        IssueIdentifier = "42",
        IssueDetail = new IssueDetail { Identifier = "42", Title = "Test", Description = "", Labels = [] },
        ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
        IssueComments = [],
        RepoProviderConfigId = "rp",
        AgentProviderConfigId = "ap",
        ProviderConfigs = [],
        PipelineConfiguration = new PipelineConfiguration(),
        InitiatedBy = "test",
        QualityGateConfigs = []
    };
}
