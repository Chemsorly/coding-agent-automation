using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Api.Dispatch;

/// <summary>What the Work page's pending query derives hold texts from (spec 056 DD7).</summary>
internal sealed record PendingHoldSources(AgentCapacityReader Capacity, CredentialPauseStore Pauses);

/// <summary>
/// Spec 056 DD7 (S5): the Scheduler's loop never sends some items in a cycle (behind the global limit, a stopped
/// template or a stopped credential), so they get no fresh hold. The Work page derives a text for an item without a
/// stored hold, in this order: its last credential is paused; the limit for all agents is reached; its last job
/// template is at its limit. The capacity snapshot and the pauses are read once per request, without a lock.
/// </summary>
internal static class PendingHoldTexts
{
    public static async Task<List<PendingWorkItemDto>> DeriveAsync(
        List<PendingWorkItemDto> items, PendingHoldSources sources, CancellationToken ct)
    {
        var pauses = (await sources.Pauses.LoadAllAsync(ct)).ToDictionary(p => p.Credential, StringComparer.Ordinal);
        var page = await sources.Capacity.ReadPageAsync(ct);
        var global = page.CountsAvailable ? LimitText(page.All, LimitScope.Global, "") : null;
        var byTemplate = page.CountsAvailable
            ? page.Templates
                .Where(r => r.TemplateKey is not null)
                .Select(r => (Key: r.TemplateKey!, Text: LimitText(r, LimitScope.Template, r.TemplateKey!)))
                .Where(x => x.Text is not null)
                .ToDictionary(x => x.Key, x => x.Text!, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return items.Select(item =>
        {
            if (item.HoldReason is not null)
                return item;
            var text = item.Credential is { } credential && pauses.TryGetValue(credential, out var pause)
                ? HoldTexts.CredentialPaused(pause)
                : global ?? byTemplate.GetValueOrDefault(JobTemplateStore.NormalizeLabels(item.AgentSelector ?? ""));
            return text is null ? item : item with { HoldText = text };
        }).ToList();
    }

    /// <summary>The limit text of <paramref name="row"/> when its running agents reached its limit; null otherwise.</summary>
    private static string? LimitText(UiAgentCapacityRow row, LimitScope scope, string templateKey)
    {
        if (row.EffectiveLimit is not { } limit)
            return null;
        var running = row.RunningRuns + row.RunningChats;
        if (running < limit)
            return null;
        var source = string.Equals(row.EffectiveSource, AgentCapacityRules.TagValue(LimitSource.Ui), StringComparison.Ordinal)
            ? LimitSource.Ui
            : LimitSource.Helm;
        return HoldTexts.Limit(new AgentStartDecision(false, scope, source, running, limit), templateKey);
    }
}
