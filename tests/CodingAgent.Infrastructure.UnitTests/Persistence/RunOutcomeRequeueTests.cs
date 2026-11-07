using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Infrastructure.UnitTests.Persistence;

/// <summary>Spec 056 req 8.2 (task 6.4): a run ended for a paused credential counts as <c>requeued</c>.</summary>
public sealed class RunOutcomeRequeueTests
{
    [Fact]
    public void WhenProviderUnavailable_ShouldBeRequeued()
    {
        RunOutcomeTelemetry.DeriveOutcome(WorkItemStatus.Failed, null, FailureReason.ProviderUnavailable)
            .Should().Be(("requeued", "provider_unavailable"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenProviderUnavailable_ShouldWinOverAPullRequestFromExternalCi(bool draft)
    {
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/org/repo/pull/1",
            IsDraftPr = draft,
        };

        RunOutcomeTelemetry.DeriveOutcome(WorkItemStatus.Failed, payload, FailureReason.ProviderUnavailable)
            .Should().Be(("requeued", "provider_unavailable"));
    }
}
