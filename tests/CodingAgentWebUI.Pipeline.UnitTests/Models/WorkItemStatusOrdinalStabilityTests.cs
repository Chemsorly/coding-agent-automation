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
    [Fact]
    public void Pending_HasOrdinal_0()
    {
        ((int)WorkItemStatus.Pending).Should().Be(0);
    }

    [Fact]
    public void Dispatched_HasOrdinal_1()
    {
        ((int)WorkItemStatus.Dispatched).Should().Be(1);
    }

    [Fact]
    public void Running_HasOrdinal_2()
    {
        ((int)WorkItemStatus.Running).Should().Be(2);
    }

    [Fact]
    public void Succeeded_HasOrdinal_3()
    {
        ((int)WorkItemStatus.Succeeded).Should().Be(3);
    }

    [Fact]
    public void Failed_HasOrdinal_4()
    {
        ((int)WorkItemStatus.Failed).Should().Be(4);
    }

    [Fact]
    public void Cancelled_HasOrdinal_5()
    {
        ((int)WorkItemStatus.Cancelled).Should().Be(5);
    }

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
