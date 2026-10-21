using CodingAgent.Web.Ui;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Services;

/// <summary>An MCP server whose most recent run had a problem with it.</summary>
/// <param name="Reason">"unavailable (needs-auth)", "unavailable (failed)" or "all 3 calls failed (connection 2, auth 1)".</param>
/// <param name="ProblemRuns">Problem runs in a row, newest first, until the last run that used the server without one.</param>
public sealed record McpServerProblem(string Server, string Reason, int ProblemRuns, PipelineRunSummary LastProblemRun);

/// <summary>
/// Aggregation result produced by <see cref="AttentionAggregator.Aggregate"/>.
/// </summary>
public sealed record AttentionResult(
    IReadOnlyList<PipelineRunSummary> NeedsRefinement,
    IReadOnlyList<PipelineRunSummary> FailedRuns,
    IReadOnlyList<PipelineRunSummary> PlansToApprove,
    IReadOnlyList<TriageListItem>? Triages = null,
    IReadOnlyList<McpServerProblem>? McpProblems = null)
{
    /// <summary>Triages waiting for a person: an RCA to review or questions to answer.</summary>
    public IReadOnlyList<TriageListItem> TriagesNeedingYou { get; } = Triages ?? [];

    /// <summary>MCP servers whose most recent run had a problem with them.</summary>
    public IReadOnlyList<McpServerProblem> McpServerProblems { get; } = McpProblems ?? [];

    /// <summary>Sum of all section counts — drives the top-bar badge.</summary>
    public int TotalCount => NeedsRefinement.Count + FailedRuns.Count + PlansToApprove.Count + TriagesNeedingYou.Count + McpServerProblems.Count;
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

        // MCP server problems use ALL items (including consolidation runs, which carry no tool data
        // and are therefore neutral for every server).
        var mcpProblems = FindMcpServerProblems(items);

        return new AttentionResult(needsRefinement, failedRuns, plansToApprove, triagesNeedingYou, mcpProblems);
    }

    /// <summary>
    /// Finds MCP servers whose most recent non-neutral run had a problem.
    /// </summary>
    /// <remarks>
    /// For each server across all runs, the outcome is:
    /// <list type="bullet">
    ///   <item>Problem (unavailable) — server appears in UnavailableMcpServers in any phase.</item>
    ///   <item>Problem (calls failed) — server has calls, every call failed, and none with cause "other".</item>
    ///   <item>OK — server was called but did not fail (entirely).</item>
    ///   <item>Neutral — server is configured but not called and not unavailable.</item>
    /// </list>
    /// A server is listed when its first non-neutral run (newest first) is a problem.
    /// </remarks>
    public static IReadOnlyList<McpServerProblem> FindMcpServerProblems(IReadOnlyList<PipelineRunSummary> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Sort runs newest first; runs without StartedAtOffset (default = DateTimeOffset.MinValue) sort last.
        var sortedRuns = items
            .OrderByDescending(r => r.StartedAtOffset)
            .ToList();

        // Collect all unique server names across all runs (case-insensitive), skipping "web".
        var allServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in sortedRuns)
            CollectServerNames(run, allServers);
        allServers.Remove(McpToolKeys.WebServer);

        var result = new List<McpServerProblem>();

        foreach (var server in allServers)
        {
            PipelineRunSummary? lastProblemRun = null;
            int problemRunsInARow = 0;
            bool foundFirstNonNeutral = false;

            foreach (var run in sortedRuns)
            {
                var outcome = ClassifyServer(run, server);

                if (outcome == ServerOutcome.Neutral) continue;

                if (!foundFirstNonNeutral)
                {
                    foundFirstNonNeutral = true;
                    if (outcome == ServerOutcome.Ok)
                    {
                        // Most recent non-neutral run is OK → not listed.
                        break;
                    }

                    // Most recent non-neutral run is a problem → start counting.
                    lastProblemRun = run;
                    problemRunsInARow = 1;
                }
                else
                {
                    if (outcome == ServerOutcome.Ok)
                    {
                        // Hit an OK run → stop counting consecutive problems.
                        break;
                    }

                    problemRunsInARow++;
                }
            }

            if (lastProblemRun is not null)
            {
                var reason = BuildReason(lastProblemRun, server);
                result.Add(new McpServerProblem(server, reason, problemRunsInARow, lastProblemRun));
            }
        }

        // Sort by LastProblemRun.StartedAtOffset, newest first.
        return result
            .OrderByDescending(p => p.LastProblemRun.StartedAtOffset)
            .ToList();
    }

    /// <summary>Most triages Attention lists; the Triage page shows the rest.</summary>
    public const int MaxTriages = 50;

    /// <summary>
    /// The triages of the project scope (<c>""</c> = all) that wait for a person, newest first. Attention
    /// degrades to no triage group when the triage API is unavailable.
    /// </summary>
    public static async Task<IReadOnlyList<TriageListItem>> LoadTriagesNeedingYouAsync(
        IUiTriageClient client, string? projectId, ILogger logger, CancellationToken ct)
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
            logger.LogDebug(ex, "Attention: failed to load triages");
            return [];
        }
    }

    private static string NormaliseRunType(PipelineRunType runType) =>
        runType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition
            ? DecompBucket
            : runType.ToString();

    // ── MCP helpers ──────────────────────────────────────────────────────────

    private enum ServerOutcome { Neutral, Problem, Ok }

    /// <summary>Adds all server names mentioned by <paramref name="run"/> to <paramref name="servers"/>.</summary>
    private static void CollectServerNames(PipelineRunSummary run, HashSet<string> servers)
    {
        if (run.McpServers is not null)
            foreach (var s in run.McpServers)
                servers.Add(s);

        if (run.PhaseBreakdown is null) return;

        foreach (var phase in run.PhaseBreakdown.Values)
        {
            if (phase.ToolCalls is not null)
                foreach (var key in phase.ToolCalls.Keys)
                    servers.Add(McpToolKeys.ServerOf(key));

            if (phase.UnavailableMcpServers is not null)
                foreach (var name in phase.UnavailableMcpServers.Keys)
                    servers.Add(name);
        }
    }

    /// <summary>Determines this run's outcome for <paramref name="server"/>.</summary>
    private static ServerOutcome ClassifyServer(PipelineRunSummary run, string server)
    {
        bool appearsInRun = false;
        bool isUnavailable = false;
        bool hasCalls = false;
        var total = new McpCallCounts();

        if (run.McpServers?.Any(s => string.Equals(s, server, StringComparison.OrdinalIgnoreCase)) == true)
            appearsInRun = true;

        if (run.PhaseBreakdown is not null)
        {
            foreach (var phase in run.PhaseBreakdown.Values)
            {
                if (phase.UnavailableMcpServers is not null)
                {
                    foreach (var (name, _) in phase.UnavailableMcpServers)
                    {
                        if (string.Equals(name, server, StringComparison.OrdinalIgnoreCase))
                        {
                            isUnavailable = true;
                            appearsInRun = true;
                        }
                    }
                }

                if (phase.ToolCalls is not null)
                {
                    foreach (var (key, counts) in phase.ToolCalls)
                    {
                        if (string.Equals(McpToolKeys.ServerOf(key), server, StringComparison.OrdinalIgnoreCase))
                        {
                            hasCalls = true;
                            appearsInRun = true;
                            total = total.Add(counts);
                        }
                    }
                }
            }
        }

        if (!appearsInRun) return ServerOutcome.Neutral;

        // Problem: unavailable in any phase.
        if (isUnavailable) return ServerOutcome.Problem;

        // Problem: every call failed, none with cause "other".
        if (hasCalls && total.Calls > 0 && total.Failed == total.Calls && total.FailedOther == 0)
            return ServerOutcome.Problem;

        // OK: had calls (but not all failed, or some failed with "other").
        if (hasCalls) return ServerOutcome.Ok;

        // Neutral: configured but not unavailable and not called.
        return ServerOutcome.Neutral;
    }

    /// <summary>Builds the human-readable reason string for the first problem run of <paramref name="server"/>.</summary>
    private static string BuildReason(PipelineRunSummary run, string server)
    {
        // Check unavailability first.
        if (run.PhaseBreakdown is not null)
        {
            foreach (var phase in run.PhaseBreakdown.Values)
            {
                if (phase.UnavailableMcpServers is null) continue;
                foreach (var (name, status) in phase.UnavailableMcpServers)
                {
                    if (string.Equals(name, server, StringComparison.OrdinalIgnoreCase))
                    {
                        return status == McpServerStatuses.NeedsAuth
                            ? "unavailable (needs-auth)"
                            : "unavailable (failed)";
                    }
                }
            }
        }

        // Calls failed.
        var total = AggregateToolCalls(run, server);
        var causes = new List<string>();
        if (total.FailedConnection > 0) causes.Add($"connection {total.FailedConnection}");
        if (total.FailedAuth > 0) causes.Add($"auth {total.FailedAuth}");
        if (total.FailedUnknown > 0) causes.Add($"unknown {total.FailedUnknown}");

        return $"all {total.Calls} calls failed ({string.Join(", ", causes)})";
    }

    /// <summary>Sums all tool calls for <paramref name="server"/> across all phases of <paramref name="run"/>.</summary>
    private static McpCallCounts AggregateToolCalls(PipelineRunSummary run, string server)
    {
        var total = new McpCallCounts();
        if (run.PhaseBreakdown is null) return total;

        foreach (var phase in run.PhaseBreakdown.Values)
        {
            if (phase.ToolCalls is null) continue;
            foreach (var (key, counts) in phase.ToolCalls)
            {
                if (string.Equals(McpToolKeys.ServerOf(key), server, StringComparison.OrdinalIgnoreCase))
                    total = total.Add(counts);
            }
        }

        return total;
    }
}
