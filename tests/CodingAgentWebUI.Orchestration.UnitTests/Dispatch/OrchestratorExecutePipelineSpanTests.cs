using System.Diagnostics;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Verifies that the orchestrator process emits an <c>ExecutePipeline</c> activity
/// (using <see cref="PipelineTelemetry.ActivitySource"/>) when a WorkItem run is created,
/// and that the activity is stopped when the run reaches a terminal state.
///
/// These tests cover the fix for issue #2255: the Grafana "Recent Pipeline Traces" panel
/// queries Tempo for <c>ExecutePipeline</c> spans under <c>rootServiceName="coding-agent-orchestrator"</c>.
/// Before this fix the span was only emitted from agent pods (under their own service name).
/// After this fix <see cref="PipelineRunFactory.CreateFromWorkItem"/> starts the span
/// and <see cref="RunLifecycleManager"/> stops it at terminal transitions.
/// </summary>
public class OrchestratorExecutePipelineSpanTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _stoppedActivities = [];
    private readonly List<Activity> _startedActivities = [];

    private readonly Mock<ILogger> _mockLogger = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly OrchestratorRunService _runService;
    private readonly AgentRegistryService _registry;
    private readonly AgentReservationService _dispatcher;
    private readonly RunLifecycleManager _sut;

    public OrchestratorExecutePipelineSpanTests()
    {
        // Register an ActivityListener so PipelineTelemetry.ActivitySource.StartActivity returns non-null.
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => _startedActivities.Add(a),
            ActivityStopped = a => _stoppedActivities.Add(a)
        };
        ActivitySource.AddActivityListener(_listener);

        _runService = new OrchestratorRunService(_mockLogger.Object);
        _registry = new AgentRegistryService(_mockLogger.Object);
        _dispatcher = new AgentReservationService(_registry, _mockLogger.Object);

        _sut = new RunLifecycleManager(new RunLifecycleManagerDependencies(
            _runService,
            _mockHistoryService.Object,
            _registry,
            _mockLabelService.Object,
            _dispatcher,
            _mockLogger.Object));
    }

    public void Dispose() => _listener.Dispose();

    // ── CreateFromWorkItem starts ExecutePipeline span ────────────────────────

    [Fact]
    public void CreateFromWorkItem_StartsExecutePipelineActivity_WithRunIdTag()
    {
        var request = BuildRequest("run-abc", "owner/repo#42", PipelineRunType.Implementation);

        var run = PipelineRunFactory.CreateFromWorkItem(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), request);

        run.Should().NotBeNull();
        run!.OrchestratorActivity.Should().NotBeNull("CreateFromWorkItem must start an ExecutePipeline span");
        run.OrchestratorActivity!.OperationName.Should().Be("ExecutePipeline");
        run.OrchestratorActivity.GetTagItem("pipeline.run_id").Should().NotBeNull();
    }

    [Fact]
    public void CreateFromWorkItem_ExecutePipelineActivity_HasIssueAndRunTypeTag()
    {
        var workItemId = Guid.NewGuid();
        var request = BuildRequest(workItemId.ToString(), "owner/repo#77", PipelineRunType.Review);

        var run = PipelineRunFactory.CreateFromWorkItem(workItemId, request);

        run.Should().NotBeNull();
        run!.OrchestratorActivity.Should().NotBeNull();
        run.OrchestratorActivity!.GetTagItem("pipeline.run_id").Should().Be(workItemId.ToString());
        run.OrchestratorActivity.GetTagItem("pipeline.issue").Should().Be("owner/repo#77");
        run.OrchestratorActivity.GetTagItem("pipeline.run_type").Should().NotBeNull();
    }

    [Fact]
    public void CreateFromWorkItem_ConsolidationRun_DoesNotStartActivity()
    {
        // Consolidation runs are tracked via ConsolidationRun, not PipelineRun.
        // CreateFromWorkItem returns null for consolidation — no span should be started.
        var request = BuildRequest("run-consolidation", "owner/repo#1", PipelineRunType.Consolidation,
            taskType: WorkItemTaskType.Consolidation);
        var activityCountBefore = _startedActivities.Count;

        var run = PipelineRunFactory.CreateFromWorkItem(Guid.NewGuid(), request);

        run.Should().BeNull();
        // No additional activities should have been started for the consolidation run
        _startedActivities.Count.Should().Be(activityCountBefore);
    }

    // ── Terminal transitions stop the span ────────────────────────────────────

    [Fact]
    public async Task CompleteRunAsync_StopsOrchestratorActivity()
    {
        var run = CreateRunWithActivity("run-complete");
        _runService.AddRun(run);

        await _sut.CompleteRunAsync("run-complete", WorkItemStatus.Succeeded, CancellationToken.None);

        _stoppedActivities.Should().Contain(a => a.OperationName == "ExecutePipeline",
            "CompleteRunAsync must stop the OrchestratorActivity");
    }

    [Fact]
    public async Task FailRunAsync_StopsOrchestratorActivity()
    {
        var run = CreateRunWithActivity("run-fail");
        _runService.AddRun(run);

        await _sut.FailRunAsync("run-fail", "test failure", CancellationToken.None);

        _stoppedActivities.Should().Contain(a => a.OperationName == "ExecutePipeline",
            "FailRunAsync must stop the OrchestratorActivity");
    }

    [Fact]
    public async Task CancelRunAsync_StopsOrchestratorActivity()
    {
        var run = CreateRunWithActivity("run-cancel");
        _runService.AddRun(run);

        await _sut.CancelRunAsync("run-cancel", CancellationToken.None);

        _stoppedActivities.Should().Contain(a => a.OperationName == "ExecutePipeline",
            "CancelRunAsync must stop the OrchestratorActivity");
    }

    [Fact]
    public async Task CompleteRunAsync_WhenRunNotFound_DoesNotThrow()
    {
        // No run in the service — should not throw even with no span to stop
        await _sut.Invoking(s => s.CompleteRunAsync("nonexistent-run", WorkItemStatus.Succeeded, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task CompleteRunAsync_WhenNoOrchestratorActivity_DoesNotThrow()
    {
        // A run without an OrchestratorActivity (e.g. rehydrated runs created before this feature)
        var run = CreateRunWithoutActivity("run-no-activity");
        _runService.AddRun(run);

        await _sut.Invoking(s => s.CompleteRunAsync("run-no-activity", WorkItemStatus.Succeeded, CancellationToken.None))
            .Should().NotThrowAsync();
    }

    // ── Activity is still running during run lifecycle ─────────────────────────

    [Fact]
    public void CreateFromWorkItem_ActivityIsNotStopped_BeforeTermination()
    {
        var request = BuildRequest("run-active", "owner/repo#99", PipelineRunType.Implementation);

        var run = PipelineRunFactory.CreateFromWorkItem(Guid.NewGuid(), request);

        run.Should().NotBeNull();
        run!.OrchestratorActivity.Should().NotBeNull();
        // Activity should be started but not yet stopped
        run.OrchestratorActivity!.IsStopped.Should().BeFalse(
            "the ExecutePipeline span must remain open until the run terminates");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static JobDistributionRequest BuildRequest(
        string runId,
        string issueIdentifier,
        PipelineRunType runType,
        WorkItemTaskType taskType = WorkItemTaskType.Implementation)
        => new()
        {
            RunId = runId,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = "issue-provider-1",
            RepoProviderConfigId = "repo-provider-1",
            InitiatedBy = "test",
            RunType = runType,
            TaskType = taskType,
            AgentSelector = "dotnet",
            TimeoutSeconds = 3600
        };

    private PipelineRun CreateRunWithActivity(string runId)
    {
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "provider-1",
            RepoProviderConfigId = "repo-provider-1",
            InitiatedBy = "test"
        });
        // Start the span directly (simulating what CreateFromWorkItem does)
        var activity = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline");
        activity?.SetTag("pipeline.run_id", runId);
        run.OrchestratorActivity = activity;
        return run;
    }

    private static PipelineRun CreateRunWithoutActivity(string runId)
    {
        return PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "provider-1",
            RepoProviderConfigId = "repo-provider-1",
            InitiatedBy = "test"
        });
        // OrchestratorActivity remains null (the default)
    }
}
