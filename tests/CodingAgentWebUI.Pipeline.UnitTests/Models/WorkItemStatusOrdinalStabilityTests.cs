using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Guards the integer ordinals of <see cref="WorkItemStatus"/> values.
/// The DB partial unique index in PipelineDbContext uses a raw SQL filter:
///   "Status" NOT IN (3, 4, 5)
/// which corresponds to the terminal statuses (Succeeded, Failed, Cancelled).
/// If these ordinals ever shift, the index silently excludes wrong statuses,
/// allowing duplicate active work items for the same issue.
/// </summary>
public sealed class WorkItemStatusOrdinalStabilityTests
{
    [Theory]
    [InlineData(WorkItemStatus.Pending,    0)]
    [InlineData(WorkItemStatus.Dispatched, 1)]
    [InlineData(WorkItemStatus.Running,    2)]
    [InlineData(WorkItemStatus.Succeeded,  3)]
    [InlineData(WorkItemStatus.Failed,     4)]
    [InlineData(WorkItemStatus.Cancelled,  5)]
    public void Member_HasExpectedOrdinal(WorkItemStatus status, int expectedOrdinal)
        => ((int)status).Should().Be(expectedOrdinal);

    [Fact]
    public void TerminalStatuses_MatchDbPartialIndexFilter()
    {
        // The partial unique index in PipelineDbContext.cs uses:
        //   .HasFilter("\"Status\" NOT IN (3, 4, 5)")
        // These MUST be the terminal statuses. If this test fails,
        // update the HasFilter SQL in PipelineDbContext.cs.
        var terminalOrdinals = new[]
        {
            (int)WorkItemStatus.Succeeded,
            (int)WorkItemStatus.Failed,
            (int)WorkItemStatus.Cancelled
        };

        terminalOrdinals.Should().BeEquivalentTo(new[] { 3, 4, 5 });
    }

    [Fact]
    public void EnumHasExactly6Members()
    {
        // Guard against adding new members without considering the DB index.
        // If a new status is added, this test forces the developer to verify
        // that the partial index filter in PipelineDbContext.cs is still correct.
        Enum.GetValues<WorkItemStatus>().Should().HaveCount(6);
    }
}
