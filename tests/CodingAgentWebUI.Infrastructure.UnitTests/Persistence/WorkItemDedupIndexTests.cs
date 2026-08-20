using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Pins the partial unique index that is the <em>only</em> thing preventing duplicate dispatch of
/// the same issue.
///
/// <c>DbWorkDistributorBase.InsertWorkItemAsync</c> performs no pre-check —
/// <c>IsIssueDistributedAsync</c> exists but is used solely by the UI to disable a button. The
/// insert simply runs, and a duplicate is rejected by Postgres with 23505, which
/// <c>WorkItemEndpoints.CreateWorkItem</c> maps to 409 Conflict. That is the correct design: a
/// read-then-write guard would leave a TOCTOU window between the check and the insert, and
/// concurrent dispatch is exactly the case that matters.
///
/// It also means the index is load-bearing and unguarded. The E2E suite used to assert this
/// behaviourally, but those tests ran on EF InMemory, which cannot express a filtered index — the
/// harness strips it before the tests execute, so they could only ever fail, and would have kept
/// failing even if the index were deleted from production entirely. They were removed in favour of
/// this test.
///
/// What this does not cover is Postgres actually honouring the index; that is not the risk. The
/// risk is the declaration drifting, and specifically the ordinals below.
/// </summary>
public class WorkItemDedupIndexTests
{
    private static IModel BuildModel()
    {
        // Model construction alone — no connection is opened. Npgsql rather than InMemory so the
        // relational annotations this test reads are built the way production builds them.
        var options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=none;Password=none")
            .Options;
        using var db = new PipelineDbContext(options);
        return db.Model;
    }

    private static IIndex GetDedupIndex()
    {
        var entity = BuildModel().FindEntityType(typeof(WorkItemEntity));
        entity.Should().NotBeNull("WorkItemEntity must be mapped");

        var index = entity!.GetIndexes().SingleOrDefault(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(
                new[] { nameof(WorkItemEntity.IssueIdentifier), nameof(WorkItemEntity.IssueProviderConfigId) }));

        index.Should().NotBeNull(
            "dispatch deduplication depends entirely on a unique index over " +
            "(IssueIdentifier, IssueProviderConfigId); without it, concurrent dispatch of one " +
            "issue creates several live work items");

        return index!;
    }

    [Fact]
    public void DedupIndex_IsUnique()
    {
        GetDedupIndex().IsUnique.Should().BeTrue(
            "a non-unique index would not reject the duplicate insert, and nothing else checks");
    }

    /// <summary>
    /// The filter excludes terminal statuses so an issue can be re-dispatched once its previous
    /// run finished. It is written as literal ordinals, so it is only correct for as long as
    /// <see cref="WorkItemStatus"/> keeps its current member order.
    ///
    /// That coupling is guarded here rather than by the comment above the index. Insert a value
    /// anywhere before <c>Cancelled</c> and the ordinals shift: the filter would then exclude
    /// three <em>non-terminal</em> statuses from the uniqueness check, so duplicates would be
    /// accepted while genuinely finished work stayed blocked — silently, in production, with no
    /// other test noticing.
    ///
    /// Reordering the enum is not made illegal by this test; it is made loud. A reorder also needs
    /// a data migration, since rows already carry the old ordinals.
    /// </summary>
    [Fact]
    public void DedupIndex_FilterExcludesExactlyTheTerminalStatuses()
    {
        var expected =
            $"\"Status\" NOT IN ({(int)WorkItemStatus.Succeeded}, " +
            $"{(int)WorkItemStatus.Failed}, {(int)WorkItemStatus.Cancelled})";

        GetDedupIndex().GetFilter().Should().Be(expected,
            "the index filter hardcodes status ordinals and must track the WorkItemStatus enum; " +
            "if this fails after an enum change, update the HasFilter call in PipelineDbContext " +
            "and migrate existing rows");
    }

    /// <summary>
    /// Guards the other half of the same coupling: that the statuses named in the filter really
    /// are the terminal ones. A new terminal status added to the enum without being added to the
    /// filter would block re-dispatch of an issue whose run had ended in that state.
    /// </summary>
    [Fact]
    public void DedupIndex_FilterCoversEveryTerminalStatus()
    {
        var terminal = new[] { WorkItemStatus.Succeeded, WorkItemStatus.Failed, WorkItemStatus.Cancelled };
        var filter = GetDedupIndex().GetFilter()!;

        // Parse the ordinals rather than substring-matching them: once any status reaches 10,
        // Contain("1") would be satisfied by "13" and the assertion would quietly stop meaning
        // anything.
        var excluded = System.Text.RegularExpressions.Regex.Matches(filter, @"\d+")
            .Select(m => int.Parse(m.Value))
            .ToHashSet();

        var expected = terminal.Select(s => (int)s).ToHashSet();

        excluded.Should().BeEquivalentTo(expected,
            "the filter must exclude exactly the terminal statuses — excluding a non-terminal one " +
            "would let a second work item be created while the first is still live, and omitting a " +
            "terminal one would block re-dispatch of an issue whose run had already ended");
    }
}
