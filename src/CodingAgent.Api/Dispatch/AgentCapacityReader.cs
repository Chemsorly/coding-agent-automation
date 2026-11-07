using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// Spec 057 req 6.1, 7.2: a read-only view of the running agents and the four limits, for the Capacity page and the
/// Scheduler's gauges. It lists the agent Jobs without the start lock; when the list fails it reports
/// <c>CountsAvailable = false</c> with zero counts instead of failing, so the page still shows and saves limits.
/// </summary>
internal sealed class AgentCapacityReader(
    IKubernetesJobClient jobs,
    JobTemplateStore templates,
    DispatchServiceOptions options,
    AgentCapacityStore capacity,
    Serilog.ILogger log,
    IAgentProfileStore? profiles = null,
    CredentialPauseStore? pauses = null)
{
    private sealed record Snapshot(
        AgentCapacityLimits Ui,
        IReadOnlyDictionary<string, string> ToolsByKey,
        IReadOnlyDictionary<string, int> HelmByKey,
        AgentJobCounts Counts,
        bool CountsAvailable);

    /// <summary>The rows of the Capacity page.</summary>
    public async Task<UiAgentCapacityPage> ReadPageAsync(CancellationToken ct)
    {
        var s = await ReadAsync(ct);
        var pausedByKey = await PausedTextByTemplateAsync(s.ToolsByKey, ct);
        var all = new UiAgentCapacityRow(
            null, "", Runs(s.Counts.ByTemplate.Values, s.Counts.UnknownTemplate.Runs, r => r.Runs),
            Runs(s.Counts.ByTemplate.Values, s.Counts.UnknownTemplate.Chats, r => r.Chats),
            options.MaxConcurrentAgents, s.Ui.MaxConcurrentAgents,
            Effective(options.MaxConcurrentAgents, s.Ui.MaxConcurrentAgents).Value,
            Effective(options.MaxConcurrentAgents, s.Ui.MaxConcurrentAgents).Source);
        var rows = s.HelmByKey.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(key =>
        {
            var helm = s.HelmByKey[key];
            var ui = s.Ui.MaxConcurrentByJobTemplate.GetValueOrDefault(key);
            var (runs, chats) = s.Counts.ByTemplate.GetValueOrDefault(key);
            var (value, source) = Effective(helm, ui);
            var tool = AgentToolCatalog.Find(s.ToolsByKey.GetValueOrDefault(key))?.Id ?? "";
            return new UiAgentCapacityRow(key, tool, runs, chats, helm, ui, value, source, pausedByKey.GetValueOrDefault(key));
        }).ToList();
        return new UiAgentCapacityPage(all, rows, s.Counts.UnknownTemplate.Runs, s.Counts.UnknownTemplate.Chats, s.CountsAvailable);
    }

    /// <summary>The running agents and the limits that apply, for the Scheduler's gauges.</summary>
    public async Task<AgentCapacityStatus> ReadStatusAsync(CancellationToken ct)
    {
        var page = await ReadPageAsync(ct);
        static AgentCapacityStatusRow Row(UiAgentCapacityRow r) =>
            new(r.TemplateKey, r.RunningRuns, r.RunningChats, r.EffectiveLimit, r.EffectiveSource);
        return new AgentCapacityStatus(Row(page.All), page.Templates.Select(Row).ToList(),
            page.UnknownTemplateRuns, page.UnknownTemplateChats, page.CountsAvailable);
    }

    private async Task<Snapshot> ReadAsync(CancellationToken ct)
    {
        var toolsByKey = templates.ToolsByKey();
        var helmByKey = templates.MaxConcurrentByKey();
        var ui = await capacity.LoadAsync(ct);
        try
        {
            var snapshot = await AgentJobSnapshot.ReadAsync(jobs, options.Namespace, helmByKey.Keys.ToList(), ct);
            return new Snapshot(ui, toolsByKey, helmByKey, snapshot.Counts, CountsAvailable: true);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.Warning(ex, "AgentCapacityReader: could not list the agent Jobs — showing no running agents");
            var empty = new AgentJobCounts(
                helmByKey.Keys.ToDictionary(k => k, _ => (0, 0), StringComparer.Ordinal), (0, 0), 0);
            return new Snapshot(ui, toolsByKey, helmByKey, empty, CountsAvailable: false);
        }
    }

    /// <summary>
    /// Spec 056 req 4.6: per job template, the pause text of the credentials its profiles use that are paused (a Claude
    /// template can carry both Claude credentials). Empty when no store is wired or nothing is paused.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> PausedTextByTemplateAsync(
        IReadOnlyDictionary<string, string> toolsByKey, CancellationToken ct)
    {
        if (profiles is null || pauses is null)
            return new Dictionary<string, string>();
        var active = (await pauses.LoadAllAsync(ct)).ToDictionary(p => p.Credential, StringComparer.Ordinal);
        if (active.Count == 0)
            return new Dictionary<string, string>();

        return (await profiles.LoadAgentProfilesAsync(ct))
            .Select(p => (Key: JobTemplateStore.NormalizeLabels(p.JobTemplateKey()), Credential: AgentCredentials.For(p, toolsByKey)))
            .Where(x => x.Credential is not null && active.ContainsKey(x.Credential))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => string.Join("; ", g.Select(x => x.Credential!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                    .Select(c => HoldTexts.CredentialPaused(active[c]))),
                StringComparer.Ordinal);
    }

    private static int Runs(IEnumerable<(int Runs, int Chats)> rows, int unknown, Func<(int Runs, int Chats), int> pick) =>
        rows.Sum(pick) + unknown;

    private static (int? Value, string? Source) Effective(int helm, int ui) =>
        AgentCapacityRules.Effective(helm, ui) is { } limit
            ? (limit.Value, AgentCapacityRules.TagValue(limit.Source))
            : (null, null);
}
