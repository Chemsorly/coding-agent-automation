using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>Spec 057: which limit an agent start was held or refused by.</summary>
public enum LimitScope
{
    /// <summary>The limit of one job template.</summary>
    Template,

    /// <summary>The limit for all agents together.</summary>
    Global,
}

/// <summary>Spec 057: where a limit was set.</summary>
public enum LimitSource
{
    /// <summary>The Helm chart: an infrastructure ceiling.</summary>
    Helm,

    /// <summary>Settings → Agents → Capacity: the operator's limit, never above Helm.</summary>
    Ui,
}

/// <summary>The limit that applies to one scope, and where it comes from.</summary>
public readonly record struct EffectiveLimit(int Value, LimitSource Source);

/// <summary>
/// Everything a capacity decision needs, as plain numbers (Contracts does not reference the Kubernetes types).
/// </summary>
/// <param name="HelmGlobal">The Helm global limit; 0 = none.</param>
/// <param name="UiLimits">The UI limits.</param>
/// <param name="HelmByTemplate">
/// Every deployed job template key with its Helm limit (0 = none). A key that is absent is not deployed, and its UI
/// entry is ignored.
/// </param>
/// <param name="RunningByTemplate">Running agents (runs and chats) per template key.</param>
/// <param name="RunningTotal">All running agents, including those of templates that are not deployed.</param>
public sealed record AgentCapacityInputs(
    int HelmGlobal,
    AgentCapacityLimits UiLimits,
    IReadOnlyDictionary<string, int> HelmByTemplate,
    IReadOnlyDictionary<string, int> RunningByTemplate,
    int RunningTotal);

/// <summary>The answer to "may one more agent start?".</summary>
/// <param name="Allowed">True when no limit is reached.</param>
/// <param name="Scope">The limit that was reached; null when allowed.</param>
/// <param name="Source">Where that limit comes from; null when allowed.</param>
/// <param name="Running">How many agents run in that scope.</param>
/// <param name="Limit">The limit that was reached; null when allowed.</param>
public sealed record AgentStartDecision(bool Allowed, LimitScope? Scope, LimitSource? Source, int Running, int? Limit)
{
    /// <summary>An agent may start.</summary>
    public static readonly AgentStartDecision Allow = new(true, null, null, 0, null);

    /// <summary>
    /// The dispatch reason for a held work item: <c>global_concurrency_limit</c> or <c>concurrency_limit</c>; null when
    /// allowed.
    /// </summary>
    public string? Reason
    {
        get
        {
            if (Allowed)
                return null;
            return Scope == LimitScope.Global ? AgentCapacityRules.GlobalLimitReason : AgentCapacityRules.TemplateLimitReason;
        }
    }
}

/// <summary>
/// Spec 057 req 1.5, 1.6, 3.1, 4.1: the four optional limits (per template and for all agents, each in Helm and in the
/// UI) and the decisions for work items and chats. The lowest limit that is set applies; Helm wins a tie. Pure.
/// </summary>
public static class AgentCapacityRules
{
    /// <summary>Dispatch reason when the limit for all agents is reached.</summary>
    public const string GlobalLimitReason = "global_concurrency_limit";

    /// <summary>Dispatch reason when the limit of the item's job template is reached.</summary>
    public const string TemplateLimitReason = "concurrency_limit";

    /// <summary>
    /// The limit that applies when Helm sets <paramref name="helm"/> and the UI <paramref name="ui"/>: values of 0 or
    /// less are "not set"; the lower set value wins; on a tie Helm wins (a chat refusal depends on Helm). Null when
    /// neither is set.
    /// </summary>
    public static EffectiveLimit? Effective(int helm, int ui)
    {
        var h = Math.Max(helm, 0);
        var u = Math.Max(ui, 0);
        if (h == 0 && u == 0)
            return null;
        if (u == 0 || (h > 0 && h <= u))
            return new EffectiveLimit(h, LimitSource.Helm);
        return new EffectiveLimit(u, LimitSource.Ui);
    }

    /// <summary>
    /// Whether one more pipeline agent (a run of any type) may start on <paramref name="templateKey"/>. The global limit
    /// is checked first; then the template's limit. All four limits apply.
    /// </summary>
    public static AgentStartDecision CheckWorkItem(AgentCapacityInputs inputs, string templateKey)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return Check(inputs, templateKey, inputs.UiLimits.MaxConcurrentAgents, UiTemplateLimit(inputs, templateKey));
    }

    /// <summary>
    /// Whether a chat may start on <paramref name="templateKey"/>. Only the Helm limits refuse a chat; the UI limits
    /// limit pipeline work only (spec 057 D5).
    /// </summary>
    public static AgentStartDecision CheckChat(AgentCapacityInputs inputs, string templateKey)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return Check(inputs, templateKey, uiGlobal: 0, uiTemplate: 0);
    }

    /// <summary>The lowercase value of <paramref name="scope"/> for logs, span tags and metric tags.</summary>
    public static string TagValue(LimitScope scope) => scope == LimitScope.Global ? "global" : "template";

    /// <summary>The lowercase value of <paramref name="source"/> for logs, span tags and metric tags.</summary>
    public static string TagValue(LimitSource source) => source == LimitSource.Ui ? "ui" : "helm";

    private static int UiTemplateLimit(AgentCapacityInputs inputs, string templateKey) =>
        inputs.UiLimits.MaxConcurrentByJobTemplate?.GetValueOrDefault(templateKey ?? "") ?? 0;

    private static AgentStartDecision Check(AgentCapacityInputs inputs, string templateKey, int uiGlobal, int uiTemplate)
    {
        var global = Effective(inputs.HelmGlobal, uiGlobal);
        if (global is { } g && inputs.RunningTotal >= g.Value)
            return new AgentStartDecision(false, LimitScope.Global, g.Source, inputs.RunningTotal, g.Value);

        var key = templateKey ?? "";
        if (!inputs.HelmByTemplate.TryGetValue(key, out var helmTemplate))
            return AgentStartDecision.Allow;

        var running = inputs.RunningByTemplate.GetValueOrDefault(key);
        var template = Effective(helmTemplate, uiTemplate);
        if (template is { } t && running >= t.Value)
            return new AgentStartDecision(false, LimitScope.Template, t.Source, running, t.Value);

        return AgentStartDecision.Allow;
    }
}
