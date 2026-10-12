using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.Services;

/// <summary>
/// Manages the epic dispatch drawer lifecycle: loading epics with deduplication,
/// label filtering, pagination, and decomposition dispatch. The API browses the epic tracker
/// (spec 051 2.3b, <see cref="IUiBrowseClient"/>); dispatch still runs here until 2.3c.
/// Owns and constructs the underlying DrawerStateService&lt;IssueSummary&gt; for epic items.
/// Registered as Scoped (one instance per Blazor circuit).
/// </summary>
public sealed class EpicDrawerService : IEpicDrawerService, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<EpicDrawerService>();

    private readonly IUiBrowseClient _browse;
    private readonly IDispatchOrchestrationService _dispatchOrchestration;

    private readonly DrawerStateService<IssueSummary> _epicDrawer;

    public EpicDrawerService(
        IUiBrowseClient browse,
        IDispatchOrchestrationService dispatchOrchestration)
    {
        _browse = browse;
        _dispatchOrchestration = dispatchOrchestration;

        _epicDrawer = new DrawerStateService<IssueSummary>(
            t => LoadEpicDrawerIssuesAsync(t, 1),
            LoadEpicDrawerLabelsAsync,
            // TODO: [WARNING] This callback throws InvalidOperationException rather than returning a
            // graceful error tuple. Any code path that calls DrawerState.DispatchAsync() directly
            // (e.g., a Blazor component bound to the DrawerState property) will receive an unhandled
            // runtime exception. Consider returning (false, "Use DispatchFromEpicDrawerAsync", null)
            // instead of throwing.
            (issue, template) => throw new InvalidOperationException("Use DispatchFromEpicDrawerAsync on the coordinator"),
            closeOnDispatch: true);
    }

    // ── IEpicDrawerService ──

    public DrawerStateService<IssueSummary> DrawerState => _epicDrawer;

    // ── Data loading ──

    /// <summary>
    /// Loads a page of the new and the approved epics. The API lists them from the project's epic
    /// tracker, otherwise the template's own, and merges the two lists (spec 051 2.3b).
    /// </summary>
    public async Task<string?> LoadEpicDrawerIssuesAsync(PipelineJobTemplate template, int page = 1)
    {
        _epicDrawer.Loading = true;
        _epicDrawer.Page = page;
        var ct = _epicDrawer.CancellationToken;
        try
        {
            var labels = _epicDrawer.SelectedLabels.Count > 0 ? _epicDrawer.SelectedLabels.ToList() : null;
            var result = await _browse.GetEpicsAsync(template.Id, page, labels, ct);
            _epicDrawer.Items = result.Items.ToList();
            _epicDrawer.HasMore = result.HasMore;
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _epicDrawer.Items.Clear();
            return null;
        }
        // The API's reason is the message to show ("Epic issue provider not found.", "Failed to load epics: …").
        catch (InvalidOperationException ex) { _epicDrawer.Items.Clear(); return ex.Message; }
        catch (Exception ex) { _epicDrawer.Items.Clear(); return $"Failed to load epics: {ex.Message}"; }
        finally { _epicDrawer.Loading = false; }
    }

    private async Task<string?> LoadEpicDrawerLabelsAsync(PipelineJobTemplate template)
    {
        var ct = _epicDrawer.CancellationToken;
        try
        {
            _epicDrawer.Labels = (await _browse.GetEpicLabelsAsync(template.Id, ct)).ToList();
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _epicDrawer.Labels.Clear();
            return null;
        }
        catch (Exception ex) { Logger.Warning(ex, "Failed to load epic drawer labels"); _epicDrawer.Labels.Clear(); return null; }
    }

    public void ClearEpicDrawerIssues()
    {
        _epicDrawer.Items.Clear();
        _epicDrawer.Page = 1;
        _epicDrawer.HasMore = false;
        _epicDrawer.SelectedLabels.Clear();
    }

    /// <summary>
    /// The tracker the drawer lists epics from: the project's epic tracker when it has one, otherwise the
    /// template's own tracker. Dispatch binds the run to the same tracker; the template executes the epic.
    /// </summary>
    private static string EpicTrackerFor(PipelineJobTemplate template, PipelineProject? parentProject)
        => !string.IsNullOrEmpty(parentProject?.EpicIssueProviderId)
            ? parentProject.EpicIssueProviderId
            : template.IssueProviderId;

    // ── Dispatch ──

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchDecompositionAsync(
        IssueSummary issue,
        PipelineJobTemplate template,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject)
    {
        if (!issueProviders.Any(p => p.Id == template.IssueProviderId) || !repoProviders.Any(p => p.Id == template.RepoProviderId))
            return (false, "Template references providers that no longer exist.", null);

        var epicProviderId = EpicTrackerFor(template, parentProject);
        if (!issueProviders.Any(p => p.Id == epicProviderId))
            return (false, "The project's epic issue provider no longer exists.", null);

        var phaseType = issue.Labels.Contains(AgentLabels.EpicApproved, StringComparer.OrdinalIgnoreCase)
            ? PipelineRunType.Decomposition : PipelineRunType.DecompositionAnalysis;
        var phaseLabel = phaseType == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";

        return await DrawerDispatchHelper.DispatchWithOrchestrationAsync(
            _dispatchOrchestration,
            project => _dispatchOrchestration.PrepareDecompositionDistributionRequestAsync(
                new DecompositionDispatchOrchestrationRequest
                {
                    EpicIdentifier = issue.Identifier,
                    EpicTitle = issue.Title ?? "",
                    PhaseType = phaseType,
                    IssueProviderId = epicProviderId,
                    RepoProviderId = template.RepoProviderId,
                    BrainProviderId = template.BrainProviderId,
                    InitiatedBy = DrawerDispatchHelper.ManualInitiator,
                    Project = project
                }, CancellationToken.None),
            parentProject ?? new PipelineProject { Id = "", Name = "Unknown" },
            "Could not dispatch — epic is already being processed or queued, or no agents are available.",
            $"⏳ Queued epic #{issue.Identifier} for {phaseLabel} — the job controller will start an agent pod for it",
            $"✅ Dispatched epic #{issue.Identifier} for {phaseLabel}");
    }

    // ── Drawer orchestration ──

    public async Task<string?> OpenEpicDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null)
    {
        var template = templates.FirstOrDefault(t => t.Id == templateId.Value);
        if (template == null) return null;
        return await _epicDrawer.OpenAsync(template, notifyStateChanged);
    }

    public void CloseEpicDrawer() => _epicDrawer.Close();

    public Task<string?> SwitchToEpicDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null)
    {
        return _epicDrawer.SwitchAsync(templateId, notifyStateChanged,
            () => _epicDrawer.Items.Count > 0,
            async (id, ns) =>
            {
                var template = templates.FirstOrDefault(t => t.Id == id);
                if (template == null) return null;
                return template;
            });
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromEpicDrawerAsync(
        IssueSummary issue,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject)
    {
        _epicDrawer.IsDispatching = true;
        try
        {
            if (_epicDrawer.Template == null)
                return (false, "No template selected. Please select a template first.", null);

            var (success, error, successMessage) = await DispatchDecompositionAsync(issue, _epicDrawer.Template, issueProviders, repoProviders, parentProject);
            if (success)
                _epicDrawer.Close();
            return (success, error, successMessage);
        }
        finally { _epicDrawer.IsDispatching = false; }
    }

    // ── Cross-drawer coordination ──

    public void Hide() => _epicDrawer.IsOpen = false;

    // ── IDisposable ──

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _epicDrawer.Dispose();
        _disposed = true;
    }
}
