using CodingAgent.Api.Dispatch;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Api.Consolidation;

/// <summary>
/// Spec 051 2.4b: what the Consolidation page reads and triggers, moved from the web host. The page gets
/// each template's capabilities and the refactoring scan's settings, worked out here from the stored
/// templates, projects and settings, and triggers through the API's <see cref="IConsolidationService"/>.
/// </summary>
internal sealed class ConsolidationPageService(
    IProjectStore projects,
    IPipelineConfigStore configStore,
    IConsolidationService consolidation)
{
    public async Task<UiConsolidationOverview> GetOverviewAsync(CancellationToken ct)
    {
        var allProjects = await projects.LoadProjectsAsync(ct);
        var allTemplates = await projects.LoadAllTemplatesAsync(ct);
        var config = await configStore.LoadPipelineConfigAsync(ct);
        var suggestions = await consolidation.GetHarnessSuggestionsAsync(ct);
        return BuildOverview(allProjects, allTemplates, config, suggestions);
    }

    /// <summary>
    /// The page's cards: the enabled templates of enabled projects, by project name. A template's brain
    /// is read-only by its own flag or by the global setting with its project's override.
    /// </summary>
    internal static UiConsolidationOverview BuildOverview(
        IReadOnlyList<PipelineProject> allProjects,
        IReadOnlyList<PipelineJobTemplate> allTemplates,
        PipelineConfiguration config,
        HarnessSuggestions? suggestions)
    {
        var templatesById = allTemplates.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var cards = allProjects
            .Where(p => p.Enabled)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .SelectMany(p => p.TemplateIds
                .Where(templatesById.ContainsKey)
                .Select(id => (Project: p, Template: templatesById[id])))
            .Where(x => x.Template.Enabled)
            .Select(x =>
            {
                var supportsBrain = ConsolidationTemplateFilter.SupportsBrainConsolidation(x.Template);
                return new UiConsolidationTemplate(
                    x.Template.Id,
                    x.Template.Name,
                    supportsBrain,
                    ConsolidationTemplateFilter.SupportsRefactoringDetection(x.Template),
                    supportsBrain && ConsolidationTemplateFilter.IsBrainReadOnly(x.Template, x.Project, config));
            })
            .ToList();

        var brains = allTemplates
            .Where(t => !string.IsNullOrEmpty(t.BrainProviderId))
            .ToDictionary(t => t.Id, t => t.BrainProviderId!, StringComparer.Ordinal);

        return new UiConsolidationOverview(cards, brains, suggestions);
    }

    public async Task<UiRefactoringScanSettings> GetRefactoringScanSettingsAsync(string templateId, CancellationToken ct)
    {
        var config = await configStore.LoadPipelineConfigAsync(ct);
        var project = (await projects.LoadProjectsAsync(ct)).FirstOrDefault(p => p.TemplateIds.Contains(templateId));
        return BuildRefactoringScanSettings(config, project);
    }

    /// <summary>
    /// The values the scan runs with. The hotspot lookback is not project-overridable, so it is always global.
    /// </summary>
    internal static UiRefactoringScanSettings BuildRefactoringScanSettings(PipelineConfiguration config, PipelineProject? project)
    {
        var effective = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);
        return new UiRefactoringScanSettings(
            effective.MaxRefactoringProposals,
            project?.MaxRefactoringProposals is not null && effective.MaxRefactoringProposals != config.MaxRefactoringProposals,
            (int)config.HotspotAnalysisLookback.TotalDays,
            effective.RefactoringReviewEnabled,
            project?.RefactoringReviewEnabled is not null && effective.RefactoringReviewEnabled != config.RefactoringReviewEnabled);
    }

    /// <summary>
    /// Queues the run and returns the message to show. A refused trigger (already running, a configuration
    /// error, or an unknown template; <see cref="IConsolidationService"/> logs which) throws
    /// <see cref="DispatchRefusedException"/>.
    /// </summary>
    public async Task<string> TriggerAsync(UiConsolidationTriggerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        TemplateId? templateId = string.IsNullOrEmpty(request.TemplateId) ? (TemplateId?)null : new TemplateId(request.TemplateId);

        var run = await consolidation.TriggerAsync(request.Type, templateId, ct, request.AutoDispatch, request.RefactoringFocus);
        if (run is not null)
            return $"{FormatRunType(request.Type)} queued — waiting for an idle agent.";

        string? templateName = request.TemplateId is null
            ? null
            : (await projects.LoadAllTemplatesAsync(ct)).FirstOrDefault(t => t.Id == request.TemplateId)?.Name ?? request.TemplateId;
        throw new DispatchRefusedException(request.Type switch
        {
            ConsolidationRunType.BrainConsolidation => $"Brain consolidation rejected for {templateName} — already running, a configuration error occurred, no job template matches the agent profile, or template not found.",
            ConsolidationRunType.RefactoringDetection => $"Refactoring scan rejected for {templateName} — already running, a configuration error occurred, no job template matches the agent profile, or template not found.",
            ConsolidationRunType.HarnessSuggestions => "Harness suggestions rejected — already running, a configuration error occurred, or no job template matches the agent profile.",
            _ => "Consolidation rejected.",
        });
    }

    private static string FormatRunType(ConsolidationRunType type) => type switch
    {
        ConsolidationRunType.BrainConsolidation => "Brain Consolidation",
        ConsolidationRunType.RefactoringDetection => "Refactoring Detection",
        ConsolidationRunType.HarnessSuggestions => "Harness Suggestions",
        _ => type.ToString(),
    };
}
