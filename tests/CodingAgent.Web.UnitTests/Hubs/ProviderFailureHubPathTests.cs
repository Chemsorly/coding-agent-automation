using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Health;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Spec 056 DD11, task 6.3/6.5: the hub's job completion and consolidation completion hand a provider failure to
/// <see cref="IProviderFailureHandler"/> first and never fall through to the normal completion.
/// </summary>
public sealed class ProviderFailureHubPathTests
{
    private static readonly Guid WorkItemId = Guid.NewGuid();
    private static readonly AgentProviderFailure Failure =
        AgentProviderFailure.Create(AgentProviderFailure.QuotaExhausted, null, "monthly usage limit reached");

    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IRunLifecycleManager> _lifecycle = new();
    private readonly Mock<ILabelService> _labels = new();
    private readonly Mock<IProviderFailureHandler> _handler = new();
    private readonly Mock<IHostApplicationLifetime> _appLifetime = new();

    private AgentJobLifecycleService JobService(IProviderFailureHandler? handler)
    {
        _appLifetime.SetupGet(l => l.ApplicationStopping).Returns(CancellationToken.None);
        return new AgentJobLifecycleService(new AgentJobLifecycleServiceDependencies(
            _facade.Object, _lifecycle.Object, _labels.Object, Mock.Of<IHubIssueOperations>(), Mock.Of<IChangeNotifier>(),
            _appLifetime.Object, Mock.Of<IFeedbackCommentOutbox>(), Mock.Of<ILogger>(), handler));
    }

    private HubConsolidationOperations ConsolidationOps(IProviderFailureHandler? handler) => new(
        new ModelFetchService(new AgentRegistryService(Mock.Of<ILogger>()), Mock.Of<IAgentCommunication>(), Mock.Of<ILogger>()),
        new ConsolidationBadgeService(), Mock.Of<IChangeNotifier>(), _lifecycle.Object, Mock.Of<IOrchestratorRunService>(),
        Mock.Of<ILogger>(), handler);

    private static AgentEntry Agent() => new()
    {
        AgentId = "agent-job-1",
        ConnectionId = "conn-1",
        Hostname = "host-1",
        Labels = ["dotnet"],
        Status = AgentStatus.Busy,
        RegisteredAt = DateTimeOffset.UtcNow,
        ActiveJobId = WorkItemId.ToString(),
    };

    private static JobCompletionPayload Payload(AgentProviderFailure? failure) => new()
    {
        FinalStep = PipelineStep.Failed,
        FailureReason = "kiro-cli exited with code 1",
        CompletedAt = DateTimeOffset.UtcNow,
        ProviderFailure = failure,
    };

    private void SetUpRun() => _facade.Setup(f => f.GetRun(It.IsAny<JobId>())).Returns(new PipelineRun
    {
        RunId = WorkItemId.ToString(),
        IssueIdentifier = "org/repo#1",
        IssueTitle = "t",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        AgentId = "agent-job-1",
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JobCompleted_WithAProviderFailure_ShouldGoToTheHandler_AndNotFallThrough(bool handlerWins)
    {
        SetUpRun();
        _handler.Setup(h => h.HandleAsync(WorkItemId, "agent-job-1", Failure, It.IsAny<CancellationToken>()))
            .ReturnsAsync(handlerWins);
        var agent = Agent();

        await JobService(_handler.Object).HandleJobCompletedAsync(new JobId(WorkItemId.ToString()), agent, Payload(Failure), CancellationToken.None);

        _handler.Verify(h => h.HandleAsync(WorkItemId, "agent-job-1", Failure, It.IsAny<CancellationToken>()), Times.Once);
        _lifecycle.Verify(l => l.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(), It.IsAny<CancellationToken>()), Times.Never);
        _lifecycle.Verify(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _labels.Verify(l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()), Times.Never);
        agent.ActiveJobId.Should().BeNull("the agent is released either way");
    }

    [Fact]
    public async Task JobCompleted_WithoutAProviderFailure_ShouldNotCallTheHandler()
    {
        SetUpRun();

        await JobService(_handler.Object).HandleJobCompletedAsync(new JobId(WorkItemId.ToString()), Agent(), Payload(null), CancellationToken.None);

        _handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConsolidationComplete_WithAProviderFailure_ShouldGoToTheHandler_NotFailRun()
    {
        var result = new ConsolidationJobResult
        {
            JobId = WorkItemId.ToString(),
            Success = false,
            ErrorMessage = "quota",
            ProviderFailure = Failure,
        };

        await ConsolidationOps(_handler.Object).HandleConsolidationCompleteAsync(result, Agent(), CancellationToken.None);

        _handler.Verify(h => h.HandleAsync(WorkItemId, "agent-job-1", Failure, It.IsAny<CancellationToken>()), Times.Once);
        _lifecycle.Verify(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConsolidationComplete_WithoutAProviderFailure_ShouldFailTheRunAsToday()
    {
        var result = new ConsolidationJobResult { JobId = WorkItemId.ToString(), Success = false, ErrorMessage = "boom" };

        await ConsolidationOps(_handler.Object).HandleConsolidationCompleteAsync(result, Agent(), CancellationToken.None);

        _handler.VerifyNoOtherCalls();
        _lifecycle.Verify(l => l.FailRunAsync(new RunId(WorkItemId.ToString()), "boom", It.IsAny<CancellationToken>()), Times.Once);
    }
}
