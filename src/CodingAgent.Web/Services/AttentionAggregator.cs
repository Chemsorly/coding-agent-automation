using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Services;

/// <summary>
/// Aggregation result produced by <see cref="AttentionAggregator.Aggregate"/>.
/// </summary>
public sealed record AttentionResult(
    IReadOnlyList<PipelineRunSummary> NeedsRefinement,
    IReadOnlyList<PipelineRunSummary> FailedRuns,
    IReadOnlyList<PipelineRunSummary> PlansToApprove,
    IReadOnlyList<TriageListItem>? Triages = null)
{
    /// <summary>Triages waiting for a person: an RCA to review or questions to answer.</summary>
    public IReadOnlyList<TriageListItem> TriagesNeedingYou { get; } = Triages ?? [];

    /// <summary>Sum of all section counts — drives the top-bar badge.</summary>
    public int TotalCount => NeedsRefinement.Count + FailedRuns.Count + PlansToApprove.Count + TriagesNeedingYou.Count;
}

/// <summary>
/// Pure, stateless aggregation helper that de-duplicates a flat list of
/// <see cref="PipelineRunSummary"/> objects into the three Attention sections.
///
/// Algorithm:
/// 1. Exclude <see cref="PipelineRunType.Consolidation"/> runs — they are tracked separately (#2567).
/// 2. Group by (IssueIdentifier, normalised run type):
///    - <see cref="PipelineRunType.DecompositionAnalysis"/> and <see cref="PipelineRunType.Decomposition"/>
///      merge into one bucket ("decomp") so that a Phase-2 run supersedes the Phase-1 plan.
///    - All other run types (<see cref="PipelineRunType.Implementation"/>,
///      <see cref="PipelineRunType.Review"/>) keep their own bucket per issue, because a completed
///      code review does not resolve a failed implementation.
/// 3. Pick the most-recent run per group (by <see cref="PipelineRunSummary.StartedAtOffset"/>).
/// 4. Classify the representative:
///    - NeedsRefinement  → AnalysisRecommendation == NotReady
///    - FailedRuns       → FinalStep == Failed
///    - PlansToApprove   → RunType == DecompositionAnalysis &amp;&amp; FinalStep == Completed
///      (once Phase-2 ran, the representative is a Decomposition run, automatically excluded)
/// </summary>
public static class AttentionAggregator
{
    // Intentionally does NOT equal any PipelineRunType.ToString() value
    // ("Decomposition" or "DecompositionAnalysis"), so there is no key collision.
    private const string DecompBucket = "decomp";

    /// <summary>
    /// Aggregates <paramref name="items"/> into de-duplicated attention sections.
    /// The caller is responsible for pre-filtering by project and for passing only
    /// non-active runs when that is the desired scope.
    /// </summary>
    /// <remarks>
    /// <paramref name="triages"/> (both flows) are kept when they wait for a person; they come from the triage
    /// list, not from run history, because a triage's state is set by people as well as runs.
    /// </remarks>
    public static AttentionResult Aggregate(IReadOnlyList<PipelineRunSummary> items, IReadOnlyList<TriageListItem>? triages = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Step 1: exclude Consolidation runs.
        var eligible = items.Where(r => r.RunType != PipelineRunType.Consolidation);

        // Step 2 & 3: group and pick the latest representative per group.
        // TODO: [WARNING] If a PipelineRunSummary has a null IssueIdentifier.Value (e.g. from a
        // deserialization edge-case where the JSON field is absent), all such rows will bucket under
        // the same (null, runType) key and only one representative will survive, silently discarding
        // others. Guard against this with a null-coalesce (r.IssueIdentifier.Value ?? "") or by
        // skipping rows with null identifiers explicitly.
        var representatives = eligible
            .GroupBy(r => (r.IssueIdentifier.Value, NormaliseRunType(r.RunType)))
            .Select(g => g.OrderByDescending(r => r.StartedAtOffset).First())
            .ToList();

        // Step 4: classify.
        // TODO: [WARNING] The two Where predicates below are independent, so a representative that has
        // both AnalysisRecommendation == NotReady AND FinalStep == Failed (a real scenario: confidence
        // gate fires and the run terminates as Failed) will appear in BOTH NeedsRefinement and FailedRuns.
        // TotalCount then over-counts by 1 and the badge will disagree with the Attention page (where
        // a single row is rendered once). Consider giving NeedsRefinement priority and excluding those
        // runs from FailedRuns, i.e. only count a Failed representative whose recommendation is not NotReady.
        var needsRefinement = representatives
            .Where(r => r.AnalysisRecommendation == AnalysisGateResult.NotReady)
            .ToList();

        var failedRuns = representatives
            .Where(r => r.FinalStep == PipelineStep.Failed)
            .ToList();

        var plansToApprove = representatives
            .Where(r => r.RunType == PipelineRunType.DecompositionAnalysis
                     && r.FinalStep == PipelineStep.Completed)
            .ToList();

        var triagesNeedingYou = (triages ?? [])
            .Where(t => TriageStatusResolver.IsInTab(t.Status, TriageListTab.NeedYou))
            .ToList();

        return new AttentionResult(needsRefinement, failedRuns, plansToApprove, triagesNeedingYou);
    }

    /// <summary>Most triages Attention lists; the Triage page shows the rest.</summary>
    public const int MaxTriages = 50;

    /// <summary>
    /// The triages of the project scope (<c>""</c> = all) that wait for a person, newest first. Attention
    /// degrades to no triage group when the triage API is unavailable.
    /// </summary>
    public static async Task<IReadOnlyList<TriageListItem>> LoadTriagesNeedingYouAsync(
        IPipelineApiTriageClient client, string? projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            var page = await client.ListAsync(new TriageListQuery
            {
                ProjectId = string.IsNullOrEmpty(projectId) ? null : projectId,
                Tab = TriageListTab.NeedYou,
                PageSize = MaxTriages,
            }, ct);
            return page.Items;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Attention: failed to load triages");
            return [];
        }
    }

    private static string NormaliseRunType(PipelineRunType runType) =>
        runType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition
            ? DecompBucket
            : runType.ToString();
}
