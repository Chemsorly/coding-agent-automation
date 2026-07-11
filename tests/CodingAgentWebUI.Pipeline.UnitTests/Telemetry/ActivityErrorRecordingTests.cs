using System.Diagnostics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using CodingAgentWebUI.Pipeline.Telemetry;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Telemetry;

public class ActivityErrorRecordingTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = [];

    public ActivityErrorRecordingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void RecordError_SetsErrorStatusAndAddsException()
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Test");
        var ex = new InvalidOperationException("test failure");

        activity.RecordError(ex);

        activity!.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("test failure");
        activity.Events.Should().Contain(e => e.Name == "exception");
    }

    [Fact]
    public void RecordError_GracefulCancellation_SetsCancelledTagWithoutError()
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Test");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = new OperationCanceledException(cts.Token);

        activity.RecordError(ex, cts.Token);

        activity!.Status.Should().Be(ActivityStatusCode.Unset);
        activity.GetTagItem("pipeline.cancelled").Should().Be(true);
        activity.Events.Should().BeEmpty();
    }

    [Fact]
    public void RecordError_TimeoutOce_SetsError()
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Test");
        var ex = new OperationCanceledException("timeout");
        // ct is NOT cancelled — simulates timeout-originated OCE
        var ct = CancellationToken.None;

        activity.RecordError(ex, ct);

        activity!.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("timeout");
    }

    [Fact]
    public void RecordError_NullActivity_DoesNotThrow()
    {
        Activity? activity = null;
        var ex = new InvalidOperationException("test");

        var act = () => activity.RecordError(ex);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task PipelineStepRunner_RecordsErrorOnParentActivity_WhenStepThrows()
    {
        // When a step throws, its using-var activity is disposed before the catch in
        // PipelineStepRunner fires, so Activity.Current reverts to the parent.
        // PipelineStepRunner records error on whatever Activity.Current is (the parent).
        using var parentActivity = PipelineTelemetry.ActivitySource.StartActivity("ParentSpan");
        var step = new ThrowingStep();
        var context = BuildContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PipelineStepRunner.ExecuteAsync([step], context, CancellationToken.None));

        // The parent activity gets the error (the step's own activity is already stopped)
        parentActivity!.Status.Should().Be(ActivityStatusCode.Error);
        parentActivity.StatusDescription.Should().Be("step failed");
    }

    [Fact]
    public async Task PipelineStepRunner_CancelledStep_SetsCancelledTagOnParent()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var parentActivity = PipelineTelemetry.ActivitySource.StartActivity("ParentSpan");
        var step = new CancellingStep();
        var context = BuildContext();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => PipelineStepRunner.ExecuteAsync([step], context, cts.Token));

        parentActivity!.Status.Should().Be(ActivityStatusCode.Unset);
        parentActivity.GetTagItem("pipeline.cancelled").Should().Be(true);
    }

    [Fact]
    public async Task TryCriticalAsync_RecordsErrorOnActivity_WhenActionFails()
    {
        var context = BuildContext();

        // Start an activity to simulate what a step does
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("TestCritical");

        var result = await context.TryCriticalAsync(
            () => throw new InvalidOperationException("critical failure"),
            "TestAction");

        result.Should().Be(StepResult.Stop);
        activity!.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("critical failure");
    }

    [Fact]
    public async Task TryNonCriticalAsync_RecordsErrorOnActivity_WhenActionFails()
    {
        var context = BuildContext();

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("TestNonCritical");

        var result = await context.TryNonCriticalAsync(
            () => throw new InvalidOperationException("non-critical failure"),
            "TestAction");

        result.Should().Be(StepResult.Continue);
        activity!.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("non-critical failure");
    }

    private static PipelineStepContext BuildContext()
    {
        var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Implementation,
            ProjectId = "proj",
            ProjectName = "TestProject"
        };
        var prOrchestrator = new PullRequestOrchestrator(logger);
        var callbacks = new Mock<IPipelineCallbacks>();
        callbacks.Setup(c => c.SwapAgentLabel(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ProviderConfigStore = Mock.Of<IConfigurationStore>(),
            QualityGateConfigStore = Mock.Of<IConfigurationStore>(),
            ReviewerConfigStore = Mock.Of<IConfigurationStore>(),
            IssueProvider = Mock.Of<IIssueProvider>(),
            Callbacks = callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = new AgentPhaseExecutor(logger),
            QualityGates = new QualityGateExecutor(
                Mock.Of<IQualityGateValidator>(), prOrchestrator, new CiLogWriter(logger), new FeedbackService(logger), logger),
            BrainSync = null,
            PrOrchestrator = prOrchestrator,
            Logger = logger
        };
    }

    /// <summary>Step that throws to verify error recording on parent.</summary>
    private sealed class ThrowingStep : IPipelineStep
    {
        public string StepName => "TestThrow";
        public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
            => throw new InvalidOperationException("step failed");
    }

    /// <summary>Step that throws OCE with a cancelled token.</summary>
    private sealed class CancellingStep : IPipelineStep
    {
        public string StepName => "TestCancel";
        public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(StepResult.Continue);
        }
    }

    // --- RecordMaskedError tests ---

    private static readonly Dictionary<string, string> TestSecrets = new()
    {
        { "API_KEY", "super-secret-token-1234" },
        { "DB_PASS", "my-database-password" }
    };

    [Fact]
    public void RecordMaskedError_MasksSecretInStatusDescription()
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Test");
        var ex = new InvalidOperationException("Connection failed with super-secret-token-1234");

        activity.RecordMaskedError(ex, TestSecrets);

        activity!.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().NotContain("super-secret-token-1234");
        activity.StatusDescription.Should().Contain("***");
    }

    [Fact]
    public void RecordMaskedError_MasksSecretInExceptionEvent()
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Test");
        var ex = new InvalidOperationException("Failed: my-database-password invalid");

        activity.RecordMaskedError(ex, TestSecrets);

        var exceptionEvent = activity!.Events.Should().ContainSingle(e => e.Name == "exception").Which;
        var message = exceptionEvent.Tags.First(t => t.Key == "exception.message").Value as string;
        message.Should().NotContain("my-database-password");
        message.Should().Contain("***");
    }

    [Fact]
    public void RecordMaskedError_PreservesExceptionType()
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Test");
        var ex = new InvalidOperationException("error with super-secret-token-1234");

        activity.RecordMaskedError(ex, TestSecrets);

        var exceptionEvent = activity!.Events.Should().ContainSingle(e => e.Name == "exception").Which;
        var type = exceptionEvent.Tags.First(t => t.Key == "exception.type").Value as string;
        type.Should().Be(typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public void RecordMaskedError_PreservesStackTrace()
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Test");
        Exception ex;
        try { throw new InvalidOperationException("error with super-secret-token-1234"); }
        catch (Exception caught) { ex = caught; }

        activity.RecordMaskedError(ex, TestSecrets);

        var exceptionEvent = activity!.Events.Should().ContainSingle(e => e.Name == "exception").Which;
        var stackTrace = exceptionEvent.Tags.First(t => t.Key == "exception.stacktrace").Value as string;
        stackTrace.Should().NotBeNullOrEmpty();
        stackTrace.Should().Contain("RecordMaskedError_PreservesStackTrace");
    }

    [Fact]
    public void RecordMaskedError_GracefulCancellation_SetsCancelledTag()
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Test");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = new OperationCanceledException(cts.Token);

        activity.RecordMaskedError(ex, TestSecrets, cts.Token);

        activity!.Status.Should().Be(ActivityStatusCode.Unset);
        activity.GetTagItem("pipeline.cancelled").Should().Be(true);
        activity.Events.Should().BeEmpty();
    }

    [Fact]
    public void RecordMaskedError_NullActivity_DoesNotThrow()
    {
        Activity? activity = null;
        var ex = new InvalidOperationException("error with super-secret-token-1234");

        var act = () => activity.RecordMaskedError(ex, TestSecrets);

        act.Should().NotThrow();
    }

    [Fact]
    public void RecordMaskedError_NullException_ThrowsArgumentNullException()
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Test");

        var act = () => activity.RecordMaskedError(null!, TestSecrets);

        act.Should().Throw<ArgumentNullException>().WithParameterName("ex");
    }

    [Fact]
    public void RecordMaskedError_NullSecrets_ThrowsArgumentNullException()
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Test");
        var ex = new InvalidOperationException("error");

        var act = () => activity.RecordMaskedError(ex, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("secrets");
    }
}
