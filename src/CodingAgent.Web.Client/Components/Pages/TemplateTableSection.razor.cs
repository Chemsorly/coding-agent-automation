using CodingAgent.Contracts.Ui;
using CodingAgent.Web.Ui;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.Components;

namespace CodingAgent.Web.Components.Pages;

public partial class TemplateTableSection
{
    [Parameter, EditorRequired] public IReadOnlyList<PipelineJobTemplate> Templates { get; set; } = [];
    [Parameter, EditorRequired] public IReadOnlyList<PipelineProject> Projects { get; set; } = [];

    /// <summary>
    /// Spec 049: whether the signed-in user may change template configuration (global admin). When
    /// false the toggles, edit/remove/move and add controls are not rendered. The page service
    /// enforces the same rule on the server.
    /// </summary>
    [Parameter] public bool CanEdit { get; set; } = true;
    // TODO: IssueProviders, RepoProviders, BrainProviders, and PipelineProviders should be IReadOnlyList<ProviderConfig>
    // to prevent child components from accidentally mutating parent state (same rationale as Templates/RecentlyToggled).
    // Update GetProviderDisplayName and GetLabelPreview signatures at the same time to avoid a compile error.
    [Parameter, EditorRequired] public List<ProviderConfig> IssueProviders { get; set; } = [];
    [Parameter, EditorRequired] public List<ProviderConfig> RepoProviders { get; set; } = [];
    [Parameter, EditorRequired] public List<ProviderConfig> BrainProviders { get; set; } = [];
    [Parameter, EditorRequired] public List<ProviderConfig> PipelineProviders { get; set; } = [];
    [Parameter, EditorRequired] public bool IsLoopActive { get; set; }
    [Parameter, EditorRequired] public IReadOnlySet<string> RecentlyToggled { get; set; } = new HashSet<string>();
    [Parameter, EditorRequired] public IReadOnlyDictionary<string, ConfigStatusSnapshot> TemplateStatuses { get; set; } = new Dictionary<string, ConfigStatusSnapshot>();
    [Parameter, EditorRequired] public IReadOnlyList<QualityGateConfiguration> QualityGateConfigs { get; set; } = [];
    [Parameter, EditorRequired] public IReadOnlyList<ReviewerConfiguration> ReviewerConfigs { get; set; } = [];
    /// <summary>Spec 053 req 3.4: per template Id, the route each task type resolves to.</summary>
    [Parameter, EditorRequired] public IReadOnlyDictionary<string, IReadOnlyList<UiResolvedRoute>> Routes { get; set; } = new Dictionary<string, IReadOnlyList<UiResolvedRoute>>();

    [Parameter] public bool ShowAddForm { get; set; }
    [Parameter] public TemplateFormModel AddForm { get; set; } = new();
    [Parameter] public string? FormError { get; set; }
    [Parameter] public bool ShowDeleteConfirm { get; set; }
    [Parameter] public PipelineJobTemplate? DeletingTemplate { get; set; }

    [Parameter] public EventCallback<(PipelineJobTemplate, bool)> OnToggleEnabled { get; set; }
    [Parameter] public EventCallback<(PipelineJobTemplate, bool)> OnToggleImplementation { get; set; }
    [Parameter] public EventCallback<(PipelineJobTemplate, bool)> OnToggleReview { get; set; }
    [Parameter] public EventCallback<(PipelineJobTemplate, bool)> OnToggleDecomposition { get; set; }
    [Parameter] public EventCallback<(PipelineJobTemplate, bool)> OnToggleTriage { get; set; }
    [Parameter] public EventCallback<(PipelineJobTemplate, bool)> OnToggleHousekeeping { get; set; }
    [Parameter] public EventCallback<(PipelineJobTemplate, bool)> OnToggleBranchCleanup { get; set; }
    [Parameter] public EventCallback<PipelineJobTemplate> OnConfirmRemove { get; set; }
    [Parameter] public EventCallback<(TemplateId TemplateId, string SourceProjectId, string TargetProjectId)> OnMoveTemplate { get; set; }
    [Parameter] public EventCallback OnShowAddForm { get; set; }
    [Parameter] public EventCallback OnAddTemplate { get; set; }
    [Parameter] public EventCallback OnCancelAdd { get; set; }
    [Parameter] public EventCallback OnRemoveTemplate { get; set; }
    [Parameter] public EventCallback OnCancelDelete { get; set; }

    private string? _moveMenuTemplateId;
    private string? _expandedPreviewTemplateId;
    private string? _expandedFeaturesTemplateId;

    // Number of columns: 9 with the Actions column (admins), 8 without it.
    private int _columnCount => CanEdit ? 9 : 8;

    private void ToggleMoveMenu(string? templateId) =>
        _moveMenuTemplateId = _moveMenuTemplateId == templateId ? null : templateId;

    private void ToggleLabelPreview(string templateId) =>
        _expandedPreviewTemplateId = _expandedPreviewTemplateId == templateId ? null : templateId;

    private IReadOnlyList<UiResolvedRoute> RoutesOf(string templateId) =>
        Routes.TryGetValue(templateId, out var routes) ? routes : [];

    private void ToggleFeatureExpand(string templateId) =>
        _expandedFeaturesTemplateId = _expandedFeaturesTemplateId == templateId ? null : templateId;

    private async Task MoveTemplate(TemplateId templateId, string sourceProjectId, string targetProjectId)
    {
        _moveMenuTemplateId = null;
        await OnMoveTemplate.InvokeAsync((templateId, sourceProjectId, targetProjectId));
    }

    private ConfigStatusSnapshot? GetTemplateStatus(TemplateId templateId)
    {
        TemplateStatuses.TryGetValue(templateId.Value, out var status);
        return status;
    }

    private bool IsProviderMissing(PipelineJobTemplate template) =>
        !IssueProviders.Any(p => p.Id == template.IssueProviderId)
        || !RepoProviders.Any(p => p.Id == template.RepoProviderId);

    // TODO: Update parameter type to IReadOnlyList<ProviderConfig> when the [Parameter] properties above are converted.
    private static string GetProviderDisplayName(string providerId, List<ProviderConfig> providers)
    {
        var provider = providers.FirstOrDefault(p => p.Id == providerId);
        return provider?.DisplayName ?? UiFormatters.Truncate(providerId, 12);
    }

    private List<ProjectTemplateGroup> GetTemplatesByProject()
    {
        var groups = new List<ProjectTemplateGroup>();
        var templateLookup = Templates.ToDictionary(t => t.Id);

        foreach (var project in Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var projectTemplates = project.TemplateIds
                .Where(id => templateLookup.ContainsKey(id))
                .Select(id => templateLookup[id])
                .ToList();

            if (projectTemplates.Count > 0)
                groups.Add(new ProjectTemplateGroup { Project = project, Templates = projectTemplates });
        }

        // Every template records its own project, so each one appears under that project here.

        return groups;
    }

    internal LabelPreviewResult GetLabelPreview(IReadOnlyList<string>? requiredLabels) =>
        LabelPreviewResult.For(requiredLabels, QualityGateConfigs, ReviewerConfigs);

    /// <summary>
    /// What a template's stack labels pick (spec 053 req 5.1, 5.2): the quality gate and reviewer configs whose match
    /// labels intersect them, and those with no match labels. Stack labels pick no agent profile.
    /// </summary>
    public sealed class LabelPreviewResult
    {
        public IReadOnlyList<string> Labels { get; init; } = [];
        public IReadOnlyList<string> QualityGates { get; init; } = [];
        public IReadOnlyList<string> Reviewers { get; init; } = [];

        /// <summary>The preview of <paramref name="labels"/>; empty (no preview) when there are none.</summary>
        public static LabelPreviewResult For(
            IReadOnlyList<string>? labels,
            IReadOnlyList<QualityGateConfiguration> qualityGates,
            IReadOnlyList<ReviewerConfiguration> reviewers)
        {
            if (labels is not { Count: > 0 })
                return new LabelPreviewResult();
            return new LabelPreviewResult
            {
                Labels = labels,
                QualityGates = new QualityGateResolver().Resolve(qualityGates, labels).Select(q => q.DisplayName).ToList(),
                Reviewers = new ReviewerResolver().Resolve(reviewers, labels)
                    .Select(r => $"{r.DisplayName} ({r.Agents.Count} agent{(r.Agents.Count != 1 ? "s" : "")})")
                    .ToList(),
            };
        }
    }

    public class TemplateFormModel
    {
        public string Name { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string IssueProviderId { get; set; } = "";
        public string RepoProviderId { get; set; } = "";
        public string BrainProviderId { get; set; } = "";
        public string PipelineProviderId { get; set; } = "";
        public bool HousekeepingEnabled { get; set; } = false;
        public int? HousekeepingConcurrencyLimit { get; set; } = null;
        public bool HousekeepingBranchCleanupEnabled { get; set; } = false;
        public bool BrainReadOnly { get; set; }
        public bool ImplementationEnabled { get; set; } = true;
        public bool ReviewEnabled { get; set; } = true;
        public bool DecompositionEnabled { get; set; }
        public bool TriageEnabled { get; set; }

        // ── Repository settings (moved from ProviderConfig) ───────────────
        public IReadOnlyList<string>? RequiredLabels { get; set; }
        public Dictionary<string, string>? Secrets { get; set; }
        public IReadOnlyList<SetupStep>? SetupSteps { get; set; }
        public string? SteeringContent { get; set; }
        public IReadOnlyList<string>? BlacklistedPaths { get; set; }
    }

    private sealed class ProjectTemplateGroup
    {
        public required PipelineProject Project { get; init; }
        public List<PipelineJobTemplate> Templates { get; set; } = new();
    }
}
