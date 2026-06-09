using System.Diagnostics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Tests verifying consolidation executor and agent worker spans are created
/// with correct names and tags when an ActivityListener is attached.
/// </summary>
public class PipelineTelemetryAgentSpanTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = [];

    public PipelineTelemetryAgentSpanTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Theory]
    [InlineData("BrainConsolidation.Clone")]
    [InlineData("BrainConsolidation.AgentExecution")]
    [InlineData("BrainConsolidation.DiffGeneration")]
    [InlineData("BrainConsolidation.AdversarialReview")]
    [InlineData("BrainConsolidation.Commit")]
    [InlineData("BrainConsolidation.Push")]
    [InlineData("RefactoringDetection.Clone")]
    [InlineData("RefactoringDetection.HotspotAnalysis")]
    [InlineData("RefactoringDetection.AgentExecution")]
    [InlineData("RefactoringDetection.AdversarialReview")]
    [InlineData("RefactoringDetection.CreateIssues")]
    [InlineData("HarnessSuggestion.AgentExecution")]
    [InlineData("HarnessSuggestion.WriteToFile")]
    [InlineData("HarnessSuggestion.AdversarialReview")]
    [InlineData("Agent.ReceiveJob")]
    [InlineData("Agent.ReportCompletion")]
    public void StartActivity_SpanName_CreatesActivity(string spanName)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity(spanName);

        activity.Should().NotBeNull();
        activity!.OperationName.Should().Be(spanName);
    }

    [Fact]
    public void StartActivity_WithRunIdTag_TagIsRecorded()
    {
        var runId = Guid.NewGuid().ToString();

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("BrainConsolidation.Clone");
        activity?.SetTag("pipeline.run_id", runId);

        activity.Should().NotBeNull();
        activity!.GetTagItem("pipeline.run_id").Should().Be(runId);
    }

    [Fact]
    public void Activity_SetStatus_Error_RecordsErrorStatus()
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("BrainConsolidation.Clone");
        activity?.SetStatus(ActivityStatusCode.Error, "test error");

        activity.Should().NotBeNull();
        activity!.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("test error");
    }

    [Fact]
    public void Activity_AddException_RecordsExceptionEvent()
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("BrainConsolidation.Clone");
        var ex = new InvalidOperationException("test failure");
        activity?.AddException(ex);

        activity.Should().NotBeNull();
        activity!.Events.Should().NotBeEmpty();
        activity.Events.Should().Contain(e => e.Name == "exception");
    }
}
