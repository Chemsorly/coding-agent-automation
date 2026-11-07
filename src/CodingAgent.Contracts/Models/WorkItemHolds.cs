using System.Globalization;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Spec 056 (D15, DD7, DD10): why dispatch did not start a Pending work item. Every deferral the dispatch answer
/// names is one of these and is recorded on the item (<c>WorkItems.HoldReason</c>).
/// </summary>
public static class HoldReasons
{
    /// <summary>No route resolves for the item, or its stored request cannot be read.</summary>
    public const string NoRoute = AgentRouteRefusalReason.NoRoute;

    /// <summary>The route names an agent profile that does not exist.</summary>
    public const string ProfileUnavailable = AgentRouteRefusalReason.ProfileUnavailable;

    /// <summary>The profile's job template has no known tool.</summary>
    public const string NoTool = AgentRouteRefusalReason.NoTool;

    /// <summary>The profile's job template is not in the Helm values.</summary>
    public const string NoTemplate = "no_template";

    /// <summary>The route's credential is paused.</summary>
    public const string CredentialPaused = "credential_paused";

    /// <summary>The job template's agent limit is reached (spec 057).</summary>
    public const string ConcurrencyLimit = AgentCapacityRules.TemplateLimitReason;

    /// <summary>The limit for all agents is reached (spec 057).</summary>
    public const string GlobalConcurrencyLimit = AgentCapacityRules.GlobalLimitReason;

    /// <summary>No Kiro credential PVC is free.</summary>
    public const string PvcUnavailable = "pvc_unavailable";

    /// <summary>Every hold reason.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        NoRoute, ProfileUnavailable, NoTool, NoTemplate, CredentialPaused,
        ConcurrencyLimit, GlobalConcurrencyLimit, PvcUnavailable,
    ];

    /// <summary>
    /// The holds only an operator ends (DD10): a resume or a routing change. Items held for them are fetched after all
    /// other Pending items, so they never fill the dispatch window.
    /// </summary>
    public static IReadOnlyList<string> OperatorHolds { get; } =
        [CredentialPaused, NoRoute, ProfileUnavailable, NoTool, NoTemplate];

    /// <summary>True when <paramref name="reason"/> is one of <see cref="OperatorHolds"/>.</summary>
    public static bool IsOperatorHold(string? reason) =>
        reason is not null && OperatorHolds.Contains(reason, StringComparer.Ordinal);

    /// <summary>The short label of <paramref name="reason"/> for the Work page's per-reason counts (req 4.3).</summary>
    public static string Label(string reason) => reason switch
    {
        NoRoute => "No route",
        ProfileUnavailable => "Profile missing",
        NoTool => "No tool",
        NoTemplate => "No job template",
        CredentialPaused => "Credential paused",
        ConcurrencyLimit => "Template limit",
        GlobalConcurrencyLimit => "Global limit",
        PvcUnavailable => "No Kiro slot",
        _ => reason,
    };
}

/// <summary>
/// Spec 056: the texts of the holds, shown on the Work page, in the dispatch answer, in the queueing previews and in
/// the logs. The limit texts follow spec 057's chat refusal wording.
/// </summary>
public static class HoldTexts
{
    /// <summary>The longest hold text stored on a work item (<c>HoldText varchar(500)</c>).</summary>
    public const int MaxLength = 500;

    /// <summary>The text of an item whose stored request cannot be read (req 3.3).</summary>
    public const string UnreadableRequest = "The work item's request cannot be read";

    /// <summary>The text of a Kiro item with no free credential PVC.</summary>
    public const string PvcUnavailable = "No Kiro credential is free";

    /// <summary>No route resolves for <paramref name="taskType"/> work.</summary>
    public static string NoRoute(WorkItemTaskType taskType) =>
        $"No agent route for {AgentRouteLabels.TaskType(taskType)} — set it in Settings → Agents → Routing";

    /// <summary>The profile's job template is not in the Helm values.</summary>
    public static string NoTemplate(string jobTemplateKey, string profileName) =>
        $"Job template {jobTemplateKey} of agent profile '{profileName}' does not exist in the Helm values";

    /// <summary>
    /// The text of a paused credential, for example "Kiro paused — quota used up, since 2026-11-02 06:26 UTC (until
    /// 2026-12-01 00:00 UTC)".
    /// </summary>
    public static string CredentialPaused(AgentCredentialPause pause)
    {
        ArgumentNullException.ThrowIfNull(pause);
        var name = AgentCredentials.DisplayName(pause.Credential);
        var by = string.IsNullOrWhiteSpace(pause.PausedBy) ? "an operator" : pause.PausedBy;
        var why = pause.Source == CredentialPauseSources.Manual ? $"paused by {by}" : CredentialPauseReasons.Text(pause.Reason);
        var until = pause.Until is { } end ? $" (until {Time(end)})" : "";
        return $"{name} paused — {why}, since {Time(pause.Since)}{until}";
    }

    /// <summary>
    /// The text of an item held by a spec 057 limit, for example "4 of 4 agents are running for dotnet,kiro (limit set
    /// in Settings)".
    /// </summary>
    public static string Limit(AgentStartDecision decision, string templateKey)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var where = decision.Source == LimitSource.Ui ? "Settings" : "the Helm chart";
        var limit = decision.Limit?.ToString(CultureInfo.InvariantCulture) ?? "?";
        return decision.Scope == LimitScope.Global
            ? $"{decision.Running} of {limit} agents are running (limit set in {where})"
            : $"{decision.Running} of {limit} agents are running for {templateKey} (limit set in {where})";
    }

    /// <summary><paramref name="text"/> cut to <see cref="MaxLength"/>.</summary>
    public static string Cut(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= MaxLength ? text : text[..MaxLength];
    }

    private static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}

/// <summary>
/// Spec 056: the label that puts a run's issue back in its queue. Used when a failed distribution is reverted and when
/// a provider failure queues the work again.
/// </summary>
public static class QueueLabels
{
    /// <summary>
    /// <c>agent:epic</c> for a DecompositionAnalysis, <c>agent:epic-approved</c> for a Decomposition,
    /// <c>agent:triage</c> for a Triage (never <c>agent:next</c>: that would implement a raw bug report), none for a
    /// Consolidation (it has no issue), <c>agent:next</c> for every other run type (a review's on its pull request).
    /// </summary>
    public static string? For(PipelineRunType runType) => runType switch
    {
        PipelineRunType.DecompositionAnalysis => AgentLabels.Epic,
        PipelineRunType.Decomposition => AgentLabels.EpicApproved,
        PipelineRunType.Triage => AgentLabels.Triage,
        PipelineRunType.Consolidation => null,
        _ => AgentLabels.Next,
    };
}
