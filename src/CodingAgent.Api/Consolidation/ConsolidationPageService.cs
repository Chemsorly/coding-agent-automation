using CodingAgent.Api.Dispatch;
using CodingAgent.Api.Ui;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Serilog;

namespace CodingAgent.Api.Consolidation;

/// <summary>
/// Spec 051 2.4b: what the Consolidation page reads and triggers, moved from the web host. The page gets
/// each template's capabilities and the refactoring scan's settings, worked out here from the stored
/// templates, projects and settings, and triggers through the API's <see cref="IConsolidationService"/>.
/// </summary>
internal sealed class ConsolidationPageService(
    IProjectStore projects,
    IPipelineConfigStore configStore,
    IConsolidationService consolidation,
    ConsolidationStalenessService staleness,
    DispatchRouter router)
{
    private const string PausedText = "auto paused: the last run failed";

    public async Task<UiConsolidationOverview> GetOverviewAsync(CancellationToken ct)
    {
        var allProjects = await projects.LoadProjectsAsync(ct);
        var allTemplates = await projects.LoadAllTemplatesAsync(ct);
        var config = await configStore.LoadPipelineConfigAsync(ct);
        return BuildOverview(allProjects, allTemplates, config);
    }

    /// <summary>
    /// The page's cards: the enabled templates of enabled projects, by project name. A template's brain
    /// is read-only by its own flag or by the global setting with its project's override.
    /// </summary>
    internal static UiConsolidationOverview BuildOverview(
        IReadOnlyList<PipelineProject> allProjects,
        IReadOnlyList<PipelineJobTemplate> allTemplates,
        PipelineConfiguration config)
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

        return new UiConsolidationOverview(cards, brains);
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
    /// Automatic-start counters for the Consolidation page (issue #3592). Only counters whose threshold is
    /// above zero are included. Per-template errors are caught individually so one failing provider does not
    /// hide all other counters. <c>ConsolidationStalenessService</c> is the single source of truth for all
    /// counts — no counter logic is re-implemented here.
    /// <para>
    /// Spec 053: an automatic start whose Consolidation route is refused creates no run, so nothing pauses the scope
    /// and the Scheduler tries again at every check. The counter says so ("auto refused: …") until the route is fixed,
    /// for the template the Scheduler routes the run by (for a brain, the first template of its group).
    /// </para>
    /// </summary>
    public async Task<UiConsolidationAutoCounters> GetAutoCountersAsync(CancellationToken ct)
    {
        var config = await configStore.LoadPipelineConfigAsync(ct);
        var allProjects = await projects.LoadProjectsAsync(ct);
        var allTemplates = await projects.LoadAllTemplatesAsync(ct);

        // Enabled templates of enabled projects in the same order as BuildOverview.
        var templatesById = allTemplates.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var enabledPairs = allProjects
            .Where(p => p.Enabled)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .SelectMany(p => p.TemplateIds
                .Where(templatesById.ContainsKey)
                .Select(id => (Project: p, Template: templatesById[id])))
            .Where(x => x.Template.Enabled)
            .ToList();

        var brainCounters = new Dictionary<string, UiAutoCounter>(StringComparer.Ordinal);
        var refactoringCounters = new Dictionary<string, UiAutoCounter>(StringComparer.Ordinal);
        if (config.AutoBrainConsolidationAfterRuns <= 0 && config.AutoRefactoringScanAfterClosedIssues <= 0)
            return new UiConsolidationAutoCounters(brainCounters, refactoringCounters);

        // ── Brain counters ────────────────────────────────────────────────────
        if (config.AutoBrainConsolidationAfterRuns > 0)
        {
            // Templates that share a brain share the count — compute it once per brain id.
            // TODO [WARNING]: If CountBrainRunsSinceLastConsolidationAsync succeeds for a brain id
            // (populating seenBrainCounts) but a later template sharing that brain throws in
            // IsPausedAsync, the first template's counter was added while the second is silently dropped.
            // Two templates sharing one brain can then disagree (one shows a counter, one does not),
            // which contradicts the "share a brain → share the count" invariant. Fix: compute IsPaused
            // once per brain id alongside the count, or accept that per-template errors are independent.
            var seenBrainCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (_, template) in enabledPairs.Where(x =>
                ConsolidationTemplateFilter.SupportsBrainConsolidation(x.Template) &&
                !ConsolidationTemplateFilter.IsBrainReadOnly(x.Template, x.Project, config)))
            {
                var brainId = template.BrainProviderId!;
                var scopeKey = ConsolidationService.ScopeKey(ConsolidationRunType.BrainConsolidation, brainId, null);
                try
                {
                    if (!seenBrainCounts.TryGetValue(brainId, out var count))
                    {
                        count = await staleness.CountBrainRunsSinceLastConsolidationAsync(brainId, ct);
                        seenBrainCounts[brainId] = count;
                    }
                    var paused = await staleness.IsPausedAsync(scopeKey, ct);
                    brainCounters[template.Id] = new UiAutoCounter(
                        count,
                        config.AutoBrainConsolidationAfterRuns,
                        paused ? PausedText : null);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Consolidation page: failed to load the automatic-start counter for {Scope}", scopeKey);
                }
            }
        }

        // ── Refactoring counters ──────────────────────────────────────────────
        if (config.AutoRefactoringScanAfterClosedIssues > 0)
        {
            foreach (var (project, template) in enabledPairs.Where(x =>
                ConsolidationTemplateFilter.SupportsRefactoringDetection(x.Template)))
            {
                var scopeKey = ConsolidationService.ScopeKey(ConsolidationRunType.RefactoringDetection, null, template.Id);
                try
                {
                    var paused = await staleness.IsPausedAsync(scopeKey, ct);
                    var blocked = paused ? PausedText : null;
                    if (blocked is null)
                    {
                        // Apply the same project-override logic as AutoConsolidationService.
                        var limit = project.MaxRefactoringProposals ?? config.MaxRefactoringProposals;
                        var open = await staleness.CountOpenScanIssuesAsync(template, ct);
                        if (open >= limit)
                            blocked = $"auto waiting: {open} scan issues still open";
                    }
                    var count = await staleness.CountClosedIssuesSinceLastScanAsync(template, ct);
                    refactoringCounters[template.Id] = new UiAutoCounter(
                        count,
                        config.AutoRefactoringScanAfterClosedIssues,
                        blocked);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Consolidation page: failed to load the automatic-start counter for {Scope}", scopeKey);
                }
            }
        }

        return new UiConsolidationAutoCounters(brainCounters, refactoringCounters);
    }

    /// <summary>
    /// Queues the run and returns the message to show, with why it waits when dispatch would hold it now (spec 056
    /// DD12). A refused trigger throws <see cref="DispatchRefusedException"/> with the possible causes (already running,
    /// a configuration error, an unknown template; <see cref="IConsolidationService"/> logs which).
    /// </summary>
    public async Task<string> TriggerAsync(UiConsolidationTriggerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        TemplateId? templateId = string.IsNullOrEmpty(request.TemplateId) ? (TemplateId?)null : new TemplateId(request.TemplateId);

        var run = await consolidation.TriggerAsync(request.Type, templateId, ct, request.AutoDispatch, request.RefactoringFocus);
        if (run is not null)
        {
            var waits = await WaitTextAsync(request.TemplateId, ct);
            return waits is null
                ? $"{FormatRunType(request.Type)} queued — waiting for an idle agent."
                : $"{FormatRunType(request.Type)} queued. It waits: {waits}";
        }

        string? templateName = request.TemplateId is null
            ? null
            : (await projects.LoadAllTemplatesAsync(ct)).FirstOrDefault(t => t.Id == request.TemplateId)?.Name ?? request.TemplateId;
        throw new DispatchRefusedException(request.Type switch
        {
            ConsolidationRunType.BrainConsolidation => $"Brain consolidation rejected for {templateName} — already running, a configuration error occurred, or template not found.",
            ConsolidationRunType.RefactoringDetection => $"Refactoring scan rejected for {templateName} — already running, a configuration error occurred, or template not found.",
            _ => "Consolidation rejected.",
        });
    }

    /// <summary>
    /// Spec 056 DD12: why a consolidation of <paramref name="templateId"/> would wait now (its route cannot run, or its
    /// credential is paused); null when it would start, or when the preview fails.
    /// </summary>
    private async Task<string?> WaitTextAsync(string? templateId, CancellationToken ct)
    {
        try
        {
            return await router.PreviewAsync(new JobDistributionRequest
            {
                IssueIdentifier = new IssueIdentifier("consolidation"),
                IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
                RepoProviderConfigId = "",
                InitiatedBy = InitiatedByConstants.Manual,
                TaskType = WorkItemTaskType.Consolidation,
                TimeoutSeconds = 0,
                ConsolidationTemplateId = templateId,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Consolidation page: could not preview the consolidation route of template {TemplateId}", templateId);
            return null;
        }
    }

    private static string FormatRunType(ConsolidationRunType type) => type switch
    {
        ConsolidationRunType.BrainConsolidation => "Brain Consolidation",
        ConsolidationRunType.RefactoringDetection => "Refactoring Detection",
        _ => type.ToString(),
    };
}
