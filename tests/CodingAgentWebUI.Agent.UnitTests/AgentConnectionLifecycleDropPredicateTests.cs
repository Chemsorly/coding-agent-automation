using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentConnectionLifecycle.ShouldDropBufferedMessage"/>.
/// Pure predicate logic — no I/O, no mocks required.
/// </summary>
public sealed class AgentConnectionLifecycleDropPredicateTests
{
    private static BufferedJobCompleted MakeMsg(int drainAttempts) =>
        new("job-1", new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow },
            DateTimeOffset.UtcNow, DrainAttempts: drainAttempts);

    [Theory]
    [InlineData(0, 3, false)]
    [InlineData(2, 3, false)]
    [InlineData(3, 3, true)]
    [InlineData(4, 3, true)]
    public void ShouldDropBufferedMessage_BoundaryValues(int drainAttempts, int maxDrainAttempts, bool expectedDrop)
    {
        var msg = MakeMsg(drainAttempts);
        AgentConnectionLifecycle.ShouldDropBufferedMessage(msg, maxDrainAttempts).Should().Be(expectedDrop);
    }
}
