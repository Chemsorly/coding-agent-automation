using k8s.Models;

namespace CodingAgent.Kubernetes;

/// <summary>
/// Spec 057: how many agents run, per job template and in total, split into runs and chats.
/// </summary>
/// <param name="ByTemplate">An entry for every template key passed to <see cref="AgentJobSnapshot.ReadAsync"/>, zeros included.</param>
/// <param name="UnknownTemplate">Jobs whose <c>caa/agent-selector</c> label is missing or matches no deployed template.</param>
/// <param name="Total">Every running agent Job, each counted once.</param>
public sealed record AgentJobCounts(
    IReadOnlyDictionary<string, (int Runs, int Chats)> ByTemplate,
    (int Runs, int Chats) UnknownTemplate,
    int Total);

/// <summary>
/// Spec 057 DD1, DD2: one read of the agent Jobs that have not ended, which gives both the running counts and the
/// credential PVCs those Jobs hold. Pipeline, consolidation, triage and chat Jobs all carry
/// <c>app.kubernetes.io/component=agent-job</c> and <c>caa/agent-selector</c> (the template key with <c>,</c> replaced by
/// <c>.</c>, <see cref="JobSpecBuilder"/>); model-fetch Jobs are relabelled and not counted. A Job with the
/// <c>caa/work-item-id</c> label is a run, one without is a chat.
/// </summary>
public sealed record AgentJobSnapshot(AgentJobCounts Counts, IReadOnlySet<string> HeldPvcs)
{
    /// <summary>The label every agent Job carries with its template key (commas replaced by dots).</summary>
    public const string AgentSelectorLabel = "caa/agent-selector";

    /// <summary>The label work-item Jobs carry; chat Jobs do not.</summary>
    public const string WorkItemIdLabel = "caa/work-item-id";

    /// <summary>
    /// Lists the agent Jobs of <paramref name="ns"/> and counts those that have not ended. A Job counts for every key in
    /// <paramref name="templateKeys"/> whose label value equals the Job's <c>caa/agent-selector</c>, else toward
    /// <see cref="AgentJobCounts.UnknownTemplate"/>. A null list counts as no Jobs. Exceptions of the client propagate.
    /// </summary>
    public static async Task<AgentJobSnapshot> ReadAsync(
        IKubernetesJobClient client, string ns, IReadOnlyCollection<string> templateKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(templateKeys);
        var list = await client.ListJobsAsync(ns, AgentJobPvcs.AgentJobSelector, ct);
        return From(list?.Items ?? [], templateKeys);
    }

    /// <summary>
    /// Groups of deployed keys that give the same label value (e.g. <c>a.b</c> and <c>a,b</c>); such keys share one
    /// count. Only groups of two or more.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> LabelCollisions(IEnumerable<string> keys) =>
        keys.Distinct(StringComparer.Ordinal)
            .GroupBy(LabelValue, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<string>)g.OrderBy(k => k, StringComparer.Ordinal).ToList())
            .ToList();

    /// <summary>The <c>caa/agent-selector</c> label value of a template key.</summary>
    public static string LabelValue(string templateKey) => templateKey.Replace(',', '.');

    internal static AgentJobSnapshot From(IEnumerable<V1Job> jobs, IReadOnlyCollection<string> templateKeys)
    {
        var running = jobs.Where(j => !KubernetesJobConditions.IsTerminal(j)).ToList();

        var keysByLabel = templateKeys
            .Distinct(StringComparer.Ordinal)
            .GroupBy(LabelValue, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var byTemplate = templateKeys.Distinct(StringComparer.Ordinal)
            .ToDictionary(k => k, _ => (Runs: 0, Chats: 0), StringComparer.Ordinal);
        var unknown = (Runs: 0, Chats: 0);

        foreach (var job in running)
        {
            var labels = job.Metadata?.Labels;
            var isRun = labels?.ContainsKey(WorkItemIdLabel) == true;
            if (labels is not null && labels.TryGetValue(AgentSelectorLabel, out var label)
                && label is not null && keysByLabel.TryGetValue(label, out var keys))
            {
                foreach (var key in keys)
                {
                    var c = byTemplate[key];
                    byTemplate[key] = isRun ? (c.Runs + 1, c.Chats) : (c.Runs, c.Chats + 1);
                }
            }
            else
            {
                unknown = isRun ? (unknown.Runs + 1, unknown.Chats) : (unknown.Runs, unknown.Chats + 1);
            }
        }

        var counts = new AgentJobCounts(byTemplate, unknown, running.Count);
        return new AgentJobSnapshot(counts, AgentJobPvcs.HeldBy(running));
    }
}
