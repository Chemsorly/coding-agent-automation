using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>
/// Pure unit tests for <see cref="CompletionOutcomeResolver.Resolve"/>.
/// No mocks required — the method is a pure function.
/// </summary>
public class CompletionOutcomeResolverTests
{
    private const string Fallback = "some fallback";

    // ── Status mapping ────────────────────────────────────────────────────────

    [Fact]
    public void Completed_step_returns_Succeeded_with_no_error()
    {
        var (status, errorMsg, failureReason) = CompletionOutcomeResolver.Resolve(
            PipelineStep.Completed, "irrelevant", FailureReason.AgentError, Fallback);

        status.Should().Be(WorkItemStatus.Succeeded);
        errorMsg.Should().BeNull();
        failureReason.Should().BeNull();
    }

    [Fact]
    public void Cancelled_step_returns_Cancelled_with_no_error()
    {
        var (status, errorMsg, failureReason) = CompletionOutcomeResolver.Resolve(
            PipelineStep.Cancelled, "irrelevant", FailureReason.AgentError, Fallback);

        status.Should().Be(WorkItemStatus.Cancelled);
        errorMsg.Should().BeNull();
        failureReason.Should().BeNull();
    }

    [Fact]
    public void ConflictRestart_step_returns_Succeeded_with_no_error()
    {
        // ConflictRestart is a clean auto-recovery: the pipeline re-queues the issue via
        // FinalLabel = agent:next. It must not be classified as a failure in WorkItem state
        // or metrics — the run terminated intentionally, not due to an error.
        var (status, errorMsg, failureReason) = CompletionOutcomeResolver.Resolve(
            PipelineStep.ConflictRestart, "PR conflicted with main — restarting pipeline", null, Fallback);

        status.Should().Be(WorkItemStatus.Succeeded);
        errorMsg.Should().BeNull("ConflictRestart is not a failure, so no error message should be produced");
        failureReason.Should().BeNull("ConflictRestart is not a failure, so no failure reason should be produced");
    }

    [Fact]
    public void Failed_step_returns_Failed()
    {
        var (status, _, _) = CompletionOutcomeResolver.Resolve(
            PipelineStep.Failed, null, null, Fallback);

        status.Should().Be(WorkItemStatus.Failed);
    }

    [Fact]
    public void Arbitrary_non_terminal_step_maps_to_Failed()
    {
        // Any step that isn't Completed or Cancelled hits the wildcard arm → Failed
        var (status, _, _) = CompletionOutcomeResolver.Resolve(
            PipelineStep.GeneratingCode, null, null, Fallback);

        status.Should().Be(WorkItemStatus.Failed);
    }

    // ── Error message derivation ──────────────────────────────────────────────

    [Fact]
    public void Failed_step_returns_provided_failure_reason_as_error_msg()
    {
        var (_, errorMsg, _) = CompletionOutcomeResolver.Resolve(
            PipelineStep.Failed, "explicit reason from agent", null, Fallback);

        errorMsg.Should().Be("explicit reason from agent");
    }

    [Fact]
    public void Failed_step_with_null_reason_uses_fallback_string()
    {
        var (_, errorMsg, _) = CompletionOutcomeResolver.Resolve(
            PipelineStep.Failed, null, null, "my specific fallback");

        errorMsg.Should().Be("my specific fallback");
    }

    [Fact]
    public void Non_failure_status_yields_null_error_and_null_failure_reason()
    {
        foreach (var step in new[] { PipelineStep.Completed, PipelineStep.Cancelled, PipelineStep.ConflictRestart })
        {
            var (_, errorMsg, failureReason) = CompletionOutcomeResolver.Resolve(
                step, "some reason", FailureReason.Timeout, Fallback);

            errorMsg.Should().BeNull($"step={step} should produce no error message");
            failureReason.Should().BeNull($"step={step} should produce no failure reason");
        }
    }

    // ── FailureReason/category derivation ────────────────────────────────────

    [Fact]
    public void Failed_step_with_null_category_defaults_to_AgentError()
    {
        var (_, _, failureReason) = CompletionOutcomeResolver.Resolve(
            PipelineStep.Failed, null, null, Fallback);

        failureReason.Should().Be(FailureReason.AgentError);
    }

    [Theory]
    [InlineData(FailureReason.Timeout)]
    [InlineData(FailureReason.InfrastructureFailure)]
    [InlineData(FailureReason.AgentError)]
    [InlineData(FailureReason.TokenRefreshFailure)]
    [InlineData(FailureReason.ExitCodeFailure)]
    public void Failed_step_with_explicit_category_propagates_it(FailureReason category)
    {
        var (_, _, failureReason) = CompletionOutcomeResolver.Resolve(
            PipelineStep.Failed, null, category, Fallback);

        failureReason.Should().Be(category);
    }
}
