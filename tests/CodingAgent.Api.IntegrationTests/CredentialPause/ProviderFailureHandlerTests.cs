using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Api.IntegrationTests.AgentRouting;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.CredentialPause;

/// <summary>
/// Spec 056 DD11, req 6.2–6.7 (task 6.5): a run whose credential could not be used ends once, pauses its credential
/// once and its work is queued again as a new Pending item — whichever completion path arrives first.
/// </summary>
[Collection(CredentialPauseCollection.Name)]
public sealed class ProviderFailureHandlerTests
{
    private const string Job = "agent-job-1";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 6, 26, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Enqueued = new(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Resets = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly AgentRoutingTestSupport.InMemoryFactory _db = new(Guid.NewGuid().ToString());
    private readonly SettableTime _time = new(Now);
    private readonly Mock<IRunLifecycleManager> _lifecycle = new();
    private readonly CredentialPauseStore _pauses;
    private readonly WorkItemTransitionService _transitions;

    public ProviderFailureHandlerTests()
    {
        _pauses = new CredentialPauseStore(_db, new AgentRoutingTestSupport.SemaphoreLockProvider(), _time, Serilog.Core.Logger.None);
        _transitions = new WorkItemTransitionService(_db, NullLogger<WorkItemTransitionService>.Instance);
    }

    private ProviderFailureHandler Handler() =>
        new(_db, _transitions, _pauses, _lifecycle.Object, new WorkItemStore(_db, Mock.Of<IOrchestratorRunService>()),
            _time, Serilog.Core.Logger.None);

    private WorkItemStatusTransitionService StatusService(IProviderFailureHandler? handler) =>
        new(_transitions, _lifecycle.Object, NullLogger<WorkItemStatusTransitionService>.Instance, _db, handler);

    private static AgentProviderFailure Quota(string detail = "monthly usage limit reached") =>
        AgentProviderFailure.Create(AgentProviderFailure.QuotaExhausted, Resets, detail);

    private static string FailedPayload(AgentProviderFailure? failure) =>
        JsonSerializer.Serialize(new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = Now,
            FailureReason = "kiro-cli exited with code 1",
            ProviderFailure = failure,
        }, PipelineJsonOptions.Default);

    private Task<WorkItemEntity> SeedRunningAsync(WorkItemTaskType taskType = WorkItemTaskType.Implementation) =>
        WorkItemTestData.SeedRoutedPendingAsync(_db, e =>
        {
            e.Status = WorkItemStatus.Running;
            e.K8sJobName = Job;
            e.AssignedAgentId = Job;
            e.Credential = AgentCredentials.Kiro;
            e.AgentSelector = AgentRoutingTestData.DefaultJobTemplate;
            e.RetryCount = 2;
            e.PriorityWeight = 7;
            e.OriginalEnqueuedAt = Enqueued;
        }, taskType: taskType);

    private async Task<List<WorkItemEntity>> AllAsync()
    {
        await using var db = _db.CreateDbContext();
        return await db.WorkItems.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task WhenTheRunningAgentReports_ShouldEndTheItem_PauseOnce_AndQueueTheWorkAgain()
    {
        var item = await SeedRunningAsync();

        var handled = await Handler().HandleAsync(item.Id, Job, Quota(), CancellationToken.None);

        handled.Should().BeTrue();
        var items = await AllAsync();
        items.Should().HaveCount(2);
        var old = items.Single(w => w.Id == item.Id);
        old.Status.Should().Be(WorkItemStatus.Failed);
        old.FailureReason.Should().Be(FailureReason.ProviderUnavailable);
        old.ErrorMessage.Should().StartWith("Requeued: Kiro paused — monthly usage limit reached");

        var requeued = items.Single(w => w.Id != item.Id);
        requeued.Status.Should().Be(WorkItemStatus.Pending);
        requeued.RetryCount.Should().Be(2);
        requeued.PriorityWeight.Should().Be(7);
        requeued.OriginalEnqueuedAt.Should().Be(Enqueued);
        requeued.IssueIdentifier.Should().Be(item.IssueIdentifier);
        requeued.HoldReason.Should().Be(HoldReasons.CredentialPaused);
        requeued.HoldText.Should().StartWith("Kiro paused — quota used up");
        requeued.Credential.Should().Be(AgentCredentials.Kiro);

        var pause = await _pauses.LoadAsync(AgentCredentials.Kiro, CancellationToken.None);
        pause.Should().NotBeNull();
        pause!.Source.Should().Be(CredentialPauseSources.Auto);
        pause.Reason.Should().Be(CredentialPauseReasons.QuotaExhausted);
        pause.Until.Should().Be(Resets);
        pause.WorkItemId.Should().Be(item.Id);

        _lifecycle.Verify(l => l.RequeueRunAsync(new RunId(item.Id.ToString()), It.IsAny<string>(),
            It.Is<RequeueTarget>(t => t.RunType == PipelineRunType.Implementation && t.AgentId == Job),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenHttpThenHubReportTheSameRun_ShouldRequeueExactlyOnce()
    {
        var item = await SeedRunningAsync();
        var handler = Handler();

        // The agent is still Busy and both paths race: the second sees the item Failed and stops.
        var first = await handler.HandleAsync(item.Id, Job, Quota(), CancellationToken.None);
        var second = await handler.HandleAsync(item.Id, Job, Quota("second report"), CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse();
        (await AllAsync()).Should().HaveCount(2);
        (await _pauses.LoadAsync(AgentCredentials.Kiro, CancellationToken.None))!.Detail
            .Should().Be("monthly usage limit reached");
        _lifecycle.Verify(l => l.RequeueRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<RequeueTarget>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // Review of spec 056: once the item is Failed every other path loses the swap, so a failing pause write must not
    // lose the requeue.
    [Fact]
    public async Task WhenThePauseWriteFails_TheWorkIsStillQueuedAgain_AndTheRunEnded()
    {
        var item = await SeedRunningAsync();
        var lockFails = new Mock<CodingAgent.Infrastructure.Locking.IDistributedLockProvider>();
        lockFails.Setup(l => l.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("advisory lock timed out"));
        var failingPauses = new CredentialPauseStore(_db, lockFails.Object, _time, Serilog.Core.Logger.None);
        var handler = new ProviderFailureHandler(_db, _transitions, failingPauses, _lifecycle.Object,
            new WorkItemStore(_db, Mock.Of<IOrchestratorRunService>()), _time, Serilog.Core.Logger.None);

        var handled = await handler.HandleAsync(item.Id, Job, Quota(), CancellationToken.None);

        handled.Should().BeTrue();
        var requeued = (await AllAsync()).Single(w => w.Id != item.Id);
        requeued.Status.Should().Be(WorkItemStatus.Pending);
        requeued.HoldReason.Should().Be(HoldReasons.CredentialPaused, "the intended pause still names why it waits");
        _lifecycle.Verify(l => l.RequeueRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<RequeueTarget>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenEndingTheRunFails_TheWorkIsStillQueuedAgain()
    {
        var item = await SeedRunningAsync();
        _lifecycle.Setup(l => l.RequeueRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<RequeueTarget>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("label service down"));

        var handled = await Handler().HandleAsync(item.Id, Job, Quota(), CancellationToken.None);

        handled.Should().BeTrue();
        (await AllAsync()).Should().ContainSingle(w => w.Id != item.Id && w.Status == WorkItemStatus.Pending);
    }

    [Fact]
    public async Task WhenAnotherAgentReports_ShouldDoNothing()
    {
        var item = await SeedRunningAsync();

        var handled = await Handler().HandleAsync(item.Id, "someone-else", Quota(), CancellationToken.None);

        handled.Should().BeFalse();
        (await AllAsync()).Single().Status.Should().Be(WorkItemStatus.Running);
        (await _pauses.LoadAsync(AgentCredentials.Kiro, CancellationToken.None)).Should().BeNull();
        _lifecycle.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhenAManualPauseExists_TheAutoPauseShouldKeepIt()
    {
        var manual = new AgentCredentialPause(AgentCredentials.Kiro, CredentialPauseReasons.Manual,
            CredentialPauseSources.Manual, Now.AddHours(-1), null, "maintenance", PausedBy: "adrian");
        await _pauses.PauseAsync(manual, CancellationToken.None);
        var item = await SeedRunningAsync();

        await Handler().HandleAsync(item.Id, Job, Quota(), CancellationToken.None);

        var stored = await _pauses.LoadAsync(AgentCredentials.Kiro, CancellationToken.None);
        stored!.Source.Should().Be(CredentialPauseSources.Manual);
        stored.Detail.Should().Be("maintenance");
    }

    [Fact]
    public async Task WhenAuthFails_ShouldPauseWithTheAuthReason_UntilResumed()
    {
        var item = await SeedRunningAsync();

        await Handler().HandleAsync(item.Id, Job,
            AgentProviderFailure.Create(AgentProviderFailure.AuthFailed, null, "401 invalid api key"), CancellationToken.None);

        var stored = await _pauses.LoadAsync(AgentCredentials.Kiro, CancellationToken.None);
        stored!.Reason.Should().Be(CredentialPauseReasons.AuthFailed);
        stored.Until.Should().BeNull();
    }

    [Fact]
    public async Task WhenTheJobControllerReportsSucceededLate_ShouldBeRejected()
    {
        var item = await SeedRunningAsync();
        await Handler().HandleAsync(item.Id, Job, Quota(), CancellationToken.None);

        var outcome = await StatusService(Handler()).TransitionAsync(
            item.Id, new WorkItemStatusRequest { Status = WorkItemStatus.Succeeded }, CancellationToken.None);

        outcome.Should().NotBe(StatusTransitionOutcome.Transitioned);
        (await AllAsync()).Single(w => w.Id == item.Id).Status.Should().Be(WorkItemStatus.Failed);
    }

    // ── HTTP path (WorkItemStatusTransitionService) ──────────────────────────────

    [Fact]
    public async Task HttpPath_WhenTheAgentReportsAProviderFailure_ShouldAnswerTransitioned_AndRequeue()
    {
        var item = await SeedRunningAsync();

        var outcome = await StatusService(Handler()).TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Failed, Result = FailedPayload(Quota()) },
            CancellationToken.None, agentCaller: Job);

        outcome.Should().Be(StatusTransitionOutcome.Transitioned);
        (await AllAsync()).Should().HaveCount(2);
        _lifecycle.Verify(l => l.FailRunAsync(It.IsAny<RunId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HttpPath_WhenTheRaceIsLost_ShouldAnswerAlreadyAtTarget_AndNotFallThrough()
    {
        var item = await SeedRunningAsync();
        await Handler().HandleAsync(item.Id, Job, Quota(), CancellationToken.None);
        _lifecycle.Invocations.Clear();

        var outcome = await StatusService(Handler()).TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Failed, Result = FailedPayload(Quota()) },
            CancellationToken.None, agentCaller: Job);

        outcome.Should().Be(StatusTransitionOutcome.AlreadyAtTarget);
        _lifecycle.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HttpPath_WhenTheOperatorKeyReportsAProviderFailure_ShouldBeAPlainFailed_WithoutPause()
    {
        var item = await SeedRunningAsync();
        var handler = new Mock<IProviderFailureHandler>(MockBehavior.Strict);

        await StatusService(handler.Object).TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Failed, Result = FailedPayload(Quota()) },
            CancellationToken.None, agentCaller: null);

        var items = await AllAsync();
        items.Should().ContainSingle();
        items[0].Status.Should().Be(WorkItemStatus.Failed);
        items[0].FailureReason.Should().NotBe(FailureReason.ProviderUnavailable);
        (await _pauses.LoadAsync(AgentCredentials.Kiro, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task HttpPath_WhenThePayloadHasNoProviderFailure_ShouldFailAsToday()
    {
        var item = await SeedRunningAsync();
        var handler = new Mock<IProviderFailureHandler>(MockBehavior.Strict);
        // An agent built before spec 056: the field is absent from the JSON.
        const string oldPayload = """{"finalStep":"Failed","failureReason":"kiro-cli exited with code 1","completedAt":"2026-11-02T06:26:00Z"}""";

        await StatusService(handler.Object).TransitionAsync(item.Id,
            new WorkItemStatusRequest { Status = WorkItemStatus.Failed, Result = oldPayload },
            CancellationToken.None, agentCaller: Job);

        var items = await AllAsync();
        items.Should().ContainSingle();
        items[0].Status.Should().Be(WorkItemStatus.Failed);
    }
}
