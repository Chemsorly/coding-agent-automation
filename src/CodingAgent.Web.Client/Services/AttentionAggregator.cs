using CodingAgent.Web.Ui;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Services;

/// <summary>An MCP server whose most recent run had a problem with it.</summary>
/// <param name="Server">The MCP server name.</param>
/// <param name="Reason">"unavailable (needs-auth)", "unavailable (failed)" or "all 3 calls failed (connection 2, auth 1)".</param>
/// <param name="ProblemRuns">Problem runs in a row, newest first, until the last run that used the server without one.</param>
/// <param name="LastProblemRun">The most recent run that had a problem with this server.</param>
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

        // MCP server problems use all items (including Consolidation), which carry no tool data
        // and are therefore neutral for every server.
        return new AttentionResult(needsRefinement, failedRuns, plansToApprove, triagesNeedingYou, FindMcpServerProblems(items));
    }

    /// <summary>
    /// Finds MCP servers whose most recent non-neutral run had a problem with them.
    /// Server names are compared case-insensitively. The built-in <c>web</c> server is never listed.
    /// </summary>
    /// <remarks>
    /// Per-server outcome for a run (aggregated across all phases):
    /// <list type="bullet">
    ///   <item><b>Problem: unavailable</b> — the server appears in any phase's <c>UnavailableMcpServers</c>.</item>
    ///   <item><b>Problem: calls failed</b> — the server is not unavailable, has calls, every call failed,
    ///         and none failed with cause "other" (<c>FailedOther == 0</c>).</item>
    ///   <item><b>OK</b> — the server has calls but the "calls failed" condition does not hold.</item>
    ///   <item><b>Neutral</b> — the server is configured or named in tool keys but was not called and not unavailable.</item>
    /// </list>
    /// A server is listed when its first non-neutral run (newest first) is a Problem.
    /// </remarks>
    public static IReadOnlyList<McpServerProblem> FindMcpServerProblems(IReadOnlyList<PipelineRunSummary> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Sort newest first. Runs without StartedAtOffset (DateTimeOffset.MinValue) sort last naturally.
        var sorted = items.OrderByDescending(r => r.StartedAtOffset).ToList();

        // Collect every unique server name across all runs (case-insensitive), skip "web".
        var allServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in sorted)
        {
            foreach (var s in run.McpServers ?? [])
                if (!s.Equals(McpToolKeys.WebServer, StringComparison.OrdinalIgnoreCase))
                    allServers.Add(s);

            foreach (var phase in run.PhaseBreakdown?.Values ?? Enumerable.Empty<PhaseUsage>())
            {
                if (phase.ToolCalls != null)
                    foreach (var key in phase.ToolCalls.Keys)
                    {
                        var srv = McpToolKeys.ServerOf(key);
                        if (!srv.Equals(McpToolKeys.WebServer, StringComparison.OrdinalIgnoreCase))
                            allServers.Add(srv);
                    }

                if (phase.UnavailableMcpServers != null)
                    foreach (var srv in phase.UnavailableMcpServers.Keys)
                        if (!srv.Equals(McpToolKeys.WebServer, StringComparison.OrdinalIgnoreCase))
                            allServers.Add(srv);
            }
        }

        var result = new List<McpServerProblem>();

        foreach (var server in allServers)
        {
            PipelineRunSummary? newestProblemRun = null;
            string? reason = null;
            int problemCount = 0;

            foreach (var run in sorted)
            {
                var (outcome, runReason) = ClassifyRunForServer(run, server);
                if (outcome == McpServerOutcome.Neutral) continue;
                if (outcome == McpServerOutcome.Ok) break;

                // Problem: record the newest, then keep counting consecutive ones.
                newestProblemRun ??= run;
                reason ??= runReason;
                problemCount++;
            }

            if (newestProblemRun != null)
                result.Add(new McpServerProblem(server, reason!, problemCount, newestProblemRun));
        }

        return result
            .OrderByDescending(p => p.LastProblemRun.StartedAtOffset)
            .ToList();
    }

    private enum McpServerOutcome { Neutral, Ok, Problem }

    /// <summary>
    /// Classifies a run's outcome for <paramref name="server"/> by aggregating all phases.
    /// When a server has conflicting availability statuses across phases, the first phase
    /// encountered in dictionary iteration order is used.
    /// </summary>
    private static (McpServerOutcome Outcome, string? Reason) ClassifyRunForServer(PipelineRunSummary run, string server)
    {
        string? unavailableStatus = null;
        int totalCalls = 0;
        int totalFailed = 0;
        int totalFailedOther = 0;
        int totalFailedConnection = 0;
        int totalFailedAuth = 0;
        int totalFailedUnknown = 0;

        foreach (var phase in run.PhaseBreakdown?.Values ?? Enumerable.Empty<PhaseUsage>())
        {
            // Check unavailability first; keep the status of the first phase that flags this server.
            if (unavailableStatus == null && phase.UnavailableMcpServers != null)
            {
                foreach (var kv in phase.UnavailableMcpServers)
                {
                    if (kv.Key.Equals(server, StringComparison.OrdinalIgnoreCase))
                    {
                        unavailableStatus = kv.Value;
                        break;
                    }
                }
            }

            // Aggregate tool calls for this server across all phases.
            if (phase.ToolCalls != null)
            {
                foreach (var kv in phase.ToolCalls)
                {
                    if (McpToolKeys.ServerOf(kv.Key).Equals(server, StringComparison.OrdinalIgnoreCase))
                    {
                        totalCalls += kv.Value.Calls;
                        totalFailed += kv.Value.Failed;
                        totalFailedOther += kv.Value.FailedOther;
                        totalFailedConnection += kv.Value.FailedConnection;
                        totalFailedAuth += kv.Value.FailedAuth;
                        totalFailedUnknown += kv.Value.FailedUnknown;
                    }
                }
            }
        }

        if (unavailableStatus != null)
        {
            var unavailableReason = unavailableStatus == McpServerStatuses.NeedsAuth
                ? "unavailable (needs-auth)"
                : "unavailable (failed)";
            return (McpServerOutcome.Problem, unavailableReason);
        }

        if (totalCalls > 0)
        {
            if (totalFailed == totalCalls && totalFailedOther == 0)
            {
                // Every call failed with only connection/auth/unknown causes — list the non-zero ones.
                var parts = new List<string>(3);
                if (totalFailedConnection > 0) parts.Add($"connection {totalFailedConnection}");
                if (totalFailedAuth > 0) parts.Add($"auth {totalFailedAuth}");
                if (totalFailedUnknown > 0) parts.Add($"unknown {totalFailedUnknown}");
                return (McpServerOutcome.Problem, $"all {totalCalls} calls failed ({string.Join(", ", parts)})");
            }

            return (McpServerOutcome.Ok, null);
        }

        return (McpServerOutcome.Neutral, null);
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
}
