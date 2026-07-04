using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Components;

namespace CodingAgentWebUI.Components.Pages;

public partial class TemplateTableSection
{
    // TODO: These mutable collection parameter types (List<T>, HashSet<T>) should be IReadOnlyList<T>
    // and IReadOnlySet<T> to prevent child components from accidentally mutating parent state.
    [Parameter, EditorRequired] public List<PipelineJobTemplate> Templates { get; set; } = [];
    [Parameter, EditorRequired] public IReadOnlyList<PipelineProject> Projects { get; set; } = [];
    [Parameter, EditorRequired] public List<ProviderConfig> IssueProviders { get; set; } = [];
    [Parameter, EditorRequired] public List<ProviderConfig> RepoProviders { get; set; } = [];
    [Parameter, EditorRequired] public List<ProviderConfig> BrainProviders { get; set; } = [];
    [Parameter, EditorRequired] public List<ProviderConfig> PipelineProviders { get; set; } = [];
    [Parameter, EditorRequired] public bool IsLoopActive { get; set; }
    [Parameter, EditorRequired] public HashSet<string> RecentlyToggled { get; set; } = new();
    [Parameter, EditorRequired] public IReadOnlyDictionary<string, ConfigStatusSnapshot> TemplateStatuses { get; set; } = new Dictionary<string, ConfigStatusSnapshot>();
    [Parameter, EditorRequired] public IReadOnlyList<QualityGateConfiguration> QualityGateConfigs { get; set; } = [];
    [Parameter, EditorRequired] public IReadOnlyList<ReviewerConfiguration> ReviewerConfigs { get; set; } = [];
    [Parameter, EditorRequired] public IReadOnlyList<AgentProfile> AgentProfiles { get; set; } = [];
    [Parameter, EditorRequired] public PipelineConfiguration PipelineConfig { get; set; } = new();

    [Parameter] public bool ShowAddForm { get; set; }
    [Parameter] public TemplateFormModel AddForm { get; set; } = new();
    [Parameter] public string? FormError { get; set; }
    [Parameter] public bool ShowDeleteConfirm { get; set; }
    [Parameter] public PipelineJobTemplate? DeletingTemplate { get; set; }

    [Parameter] public EventCallback<(PipelineJobTemplate, bool)> OnToggleEnabled { get; set; }
    [Parameter] public EventCallback<(PipelineJobTemplate, bool)> OnToggleImplementation { get; set; }
    [Parameter] public EventCallback<(PipelineJobTemplate, bool)> OnToggleReview { get; set; }
    [Parameter] public EventCallback<(PipelineJobTemplate, bool)> OnToggleDecomposition { get; set; }
    [Parameter] public EventCallback<PipelineJobTemplate> OnConfirmRemove { get; set; }
    [Parameter] public EventCallback<(string TemplateId, string SourceProjectId, string TargetProjectId)> OnMoveTemplate { get; set; }
    [Parameter] public EventCallback OnShowAddForm { get; set; }
    [Parameter] public EventCallback OnAddTemplate { get; set; }
    [Parameter] public EventCallback OnCancelAdd { get; set; }
    [Parameter] public EventCallback OnRemoveTemplate { get; set; }
    [Parameter] public EventCallback OnCancelDelete { get; set; }

    [Inject] private IAgentRegistryService Registry { get; set; } = default!;

    private string? _moveMenuTemplateId;
    private string? _expandedPreviewTemplateId;

    private void ToggleMoveMenu(string? templateId) =>
        _moveMenuTemplateId = _moveMenuTemplateId == templateId ? null : templateId;

    private void ToggleLabelPreview(string templateId) =>
        _expandedPreviewTemplateId = _expandedPreviewTemplateId == templateId ? null : templateId;

    private async Task MoveTemplate(string templateId, string sourceProjectId, string targetProjectId)
    {
        _moveMenuTemplateId = null;
        await OnMoveTemplate.InvokeAsync((templateId, sourceProjectId, targetProjectId));
    }

    private ConfigStatusSnapshot? GetTemplateStatus(string templateId)
    {
        TemplateStatuses.TryGetValue(templateId, out var status);
        return status;
    }

    private bool IsProviderMissing(PipelineJobTemplate template) =>
        !IssueProviders.Any(p => p.Id == template.IssueProviderId)
        || !RepoProviders.Any(p => p.Id == template.RepoProviderId);

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

        // Note: orphaned templates (on disk but not in any TemplateIds) are now claimed
        // by the Default project at startup via ClaimOrphanedTemplates(). If any still
        // appear here, it means a template was added to disk while the app was running.
        // They'll be picked up on next restart. No silent visual patching needed.

        return groups;
    }

    internal LabelPreviewResult GetLabelPreview(string repoProviderId)
    {
        var repoConfig = RepoProviders.FirstOrDefault(p => p.Id == repoProviderId);
        if (repoConfig is null)
            return new LabelPreviewResult();

        var labels = LabelResolver.ResolveRequiredLabels(repoConfig, PipelineConfig);
        if (labels.Count == 0)
            return new LabelPreviewResult();

        var qgResolver = new QualityGateResolver();
        var matchedQgs = qgResolver.Resolve(QualityGateConfigs, labels);

        var rvResolver = new ReviewerResolver();
        var matchedRvs = rvResolver.Resolve(ReviewerConfigs, labels);

        var matchedProfiles = AgentProfiles
            .Where(p => p.Enabled)
            .Where(p => p.MatchLabels.Count == 0 || p.MatchLabels.All(l => labels.Contains(l, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.MatchLabels.Count)
            .ThenByDescending(p => p.Priority)
            .ToList();

        var allAgents = Registry.GetAllAgents()
            .Where(a => !a.Disabled)
            .Where(a => labels.Count == 0 || labels.All(l => a.Labels.Contains(l, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        var onlineCount = allAgents.Count(a => a.Status != AgentStatus.Disconnected);

        return new LabelPreviewResult
        {
            Labels = labels,
            QualityGates = matchedQgs.Select(q => q.DisplayName).ToList(),
            Reviewers = matchedRvs.Select(r => $"{r.DisplayName} ({r.Agents.Count} agent{(r.Agents.Count != 1 ? "s" : "")})").ToList(),
            Profiles = matchedProfiles.Select(p => p.DisplayName).ToList(),
            Agents = allAgents.Select(a => a.AgentId).ToList(),
            OnlineAgentCount = onlineCount
        };
    }

    public sealed class LabelPreviewResult
    {
        public IReadOnlyList<string> Labels { get; init; } = [];
        public IReadOnlyList<string> QualityGates { get; init; } = [];
        public IReadOnlyList<string> Reviewers { get; init; } = [];
        public IReadOnlyList<string> Profiles { get; init; } = [];
        public IReadOnlyList<string> Agents { get; init; } = [];
        public int OnlineAgentCount { get; init; }
    }

    public class TemplateFormModel
    {
        public string Name { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string IssueProviderId { get; set; } = "";
        public string RepoProviderId { get; set; } = "";
        public string BrainProviderId { get; set; } = "";
        public string PipelineProviderId { get; set; } = "";
        public bool BrainReadOnly { get; set; }
        public bool ImplementationEnabled { get; set; } = true;
        public bool ReviewEnabled { get; set; } = true;
        public bool DecompositionEnabled { get; set; }
    }

    private class ProjectTemplateGroup
    {
        public required PipelineProject Project { get; init; }
        public List<PipelineJobTemplate> Templates { get; set; } = new();
    }
}
