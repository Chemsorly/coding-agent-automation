using CodingAgent.Pipeline.Models;

namespace CodingAgent.Contracts.Ui;

/// <summary>
/// The Consolidation page (<c>GET /api/ui/consolidation</c>, global read). The API decides what each
/// template supports, so no stored template, project or setting leaves it.
/// </summary>
/// <param name="Templates">The enabled templates of enabled projects, by project name, one card each.</param>
/// <param name="BrainProviderByTemplate">
/// The brain provider of every template that has one, enabled or not. Brain consolidation works on a
/// brain that several templates can share, so the page matches a run to a card by brain.
/// </param>
/// <param name="HarnessSuggestions">The last generated suggestions, or null when none exist.</param>
public sealed record UiConsolidationOverview(
    IReadOnlyList<UiConsolidationTemplate> Templates,
    IReadOnlyDictionary<string, string> BrainProviderByTemplate,
    HarnessSuggestions? HarnessSuggestions);

/// <summary>One template card of the Consolidation page.</summary>
/// <param name="BrainReadOnly">The brain is read-only for this template, so brain consolidation does not run from it.</param>
public sealed record UiConsolidationTemplate(
    string Id,
    string Name,
    bool SupportsBrainConsolidation,
    bool SupportsRefactoringDetection,
    bool BrainReadOnly);

/// <summary>
/// What a refactoring scan of a template will run with
/// (<c>GET /api/ui/consolidation/templates/{templateId}/refactoring-scan</c>): the global settings with
/// the template's project overrides, and which values the project overrides.
/// </summary>
public sealed record UiRefactoringScanSettings(
    int MaxProposals,
    bool MaxProposalsIsProjectOverride,
    int HotspotLookbackDays,
    bool ReviewEnabled,
    bool ReviewEnabledIsProjectOverride);

/// <summary>
/// Body of <c>POST /api/ui/consolidation/runs</c>. <paramref name="TemplateId"/> is null for harness
/// suggestions, which are global; <paramref name="AutoDispatch"/> labels a refactoring scan's issues
/// <c>agent:next</c>.
/// </summary>
public sealed record UiConsolidationTriggerRequest(ConsolidationRunType Type, string? TemplateId, bool AutoDispatch);
