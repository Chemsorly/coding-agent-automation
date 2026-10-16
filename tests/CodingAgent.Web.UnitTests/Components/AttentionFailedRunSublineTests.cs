using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Components.Pages;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Unit tests for Attention.FailedRunSubline (the internal static method),
/// verifying both the diagnosis-based path and the legacy fallback paths.
/// </summary>
public class AttentionFailedRunSublineTests
{
    private static readonly DateTimeOffset BaseTime =
        new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static PipelineRunSummary FailedRun(
        FailureReason? failureCategory = null,
        string? failureReason = null,
        int retryCount = 0) =>
        new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "1",
            IssueTitle = "Test issue",
            FinalStep = PipelineStep.Failed,
            FailureCategory = failureCategory,
            FailureReason = failureReason,
            RetryCount = retryCount,
            StartedAtOffset = BaseTime,
        };

    /// <summary>
    /// When a diagnosis is provided, the subline is "{Verdict}: {Title}".
    /// Acceptance criterion: Timeout → "Configuration: The run hit the agent timeout".
    /// </summary>
    [Fact]
    public void FailedRunSubline_WithTimeoutDiagnosis_ReturnsVerdictAndTitle()
    {
        var run = FailedRun(failureCategory: FailureReason.Timeout);
        var diagnosis = RunDiagnoser.Diagnose(run);

        var subline = Attention.FailedRunSubline(run, diagnosis);

        Assert.Equal("Configuration: The run hit the agent timeout", subline);
    }

    /// <summary>Null diagnosis with a FailureReason → returns the failure reason.</summary>
    [Fact]
    public void FailedRunSubline_WithNullDiagnosis_HasFailureReason_ReturnsFailureReason()
    {
        var run = FailedRun(failureReason: "some diagnostic reason");

        var subline = Attention.FailedRunSubline(run, null);

        Assert.Equal("some diagnostic reason", subline);
    }

    /// <summary>Null diagnosis with retry count → "Failed after N retries".</summary>
    [Fact]
    public void FailedRunSubline_WithNullDiagnosis_HasRetries_ReturnsFailedAfterRetries()
    {
        var run = FailedRun(retryCount: 2);

        var subline = Attention.FailedRunSubline(run, null);

        Assert.Equal("Failed after 2 retries", subline);
    }

    /// <summary>Null diagnosis, no failure reason, no retries → "Failed".</summary>
    [Fact]
    public void FailedRunSubline_WithNullDiagnosis_NeitherReasonNorRetries_ReturnsFailed()
    {
        var run = FailedRun();

        var subline = Attention.FailedRunSubline(run, null);

        Assert.Equal("Failed", subline);
    }
}
