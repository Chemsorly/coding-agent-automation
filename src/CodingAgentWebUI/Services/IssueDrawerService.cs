using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Manages the issue dispatch drawer lifecycle: loading issues, dependency checking,
/// label filtering, pagination, active-issue tracking, and dispatch.
/// Owns and constructs the underlying DrawerStateService&lt;IssueSummary&gt;.
/// Registered as Scoped (one instance per Blazor circuit).
/// </summary>
public sealed class IssueDrawerService : IIssueDrawerService, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<IssueDrawerService>();

    private readonly IProviderFactory _providerFactory;
    private readonly IDependencyChecker _dependencyChecker;
    private readonly IWorkDistributor _workDistributor;
    private readonly IDispatchOrchestrationService _dispatchOrchestration;

    private readonly DrawerStateService<IssueSummary> _issueDrawer;

    public IssueDrawerService(
        IProviderFactory providerFactory,
        IDependencyChecker dependencyChecker,
        IWorkDistributor workDistributor,
        IDispatchOrchestrationService dispatchOrchestration)
    {
        _providerFactory = providerFactory;
        _dependencyChecker = dependencyChecker;
        _workDistributor = workDistributor;
        _dispatchOrchestration = dispatchOrchestration;

        _issueDrawer = new DrawerStateService<IssueSummary>(
            LoadDrawerIssuesCallbackAsync,
            LoadDrawerLabelsCallbackAsync,
            // TODO: [WARNING] This callback throws InvalidOperationException rather than returning a
            // graceful error tuple. Any code path that calls DrawerState.DispatchAsync() directly
            // (e.g., a Blazor component bound to the DrawerState property) will receive an unhandled
            // runtime exception with no indication of the correct call path. Consider returning
            // (false, "Use DispatchFromIssueDrawerAsync", null) instead of throwing.
            (issue, template) => throw new InvalidOperationException("Use DispatchFromIssueDrawerAsync on the coordinator"),
            closeOnDispatch: true,
            postLoadAsync: CheckDrawerDependenciesInBackgroundAsync);
    }

    // ── IIssueDrawerService ──

    public DrawerStateService<IssueSummary> DrawerState => _issueDrawer;

    public Dictionary<string, DependencyCheckResult> DrawerReadiness { get; private set; } = new();

    public HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)> ActiveIssues { get; private set; } = new();

    // ── Data loading ──

    /// <summary>Private 1-arg callback passed to DrawerStateService (page=1 load).</summary>
    private async Task<string?> LoadDrawerIssuesCallbackAsync(PipelineJobTemplate template)
        => await LoadDrawerIssuesAsync(template, 1);

    public async Task<string?> LoadDrawerIssuesAsync(PipelineJobTemplate template, int page)
    {
        _issueDrawer.Loading = true;
        _issueDrawer.Page = page;
        var ct = _issueDrawer.CancellationToken;
        try
        {
            var providerConfig = _cachedIssueProviders?.FirstOrDefault(p => p.Id == template.IssueProviderId);
            if (providerConfig == null) { _issueDrawer.Loading = false; return "Issue provider not found for this template."; }
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = _issueDrawer.SelectedLabels.Count > 0 ? _issueDrawer.SelectedLabels : null;
            var result = await provider.ListOpenIssuesAsync(_issueDrawer.Page, 15, labels, ct);
            _issueDrawer.Items = result.Items.ToList();
            _issueDrawer.HasMore = result.HasMore;
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _issueDrawer.Items.Clear();
            return null;
        }
        catch (Exception ex) { _issueDrawer.Items.Clear(); return $"Failed to load issues: {ex.Message}"; }
        finally { _issueDrawer.Loading = false; }
    }

    public Task<string?> LoadDrawerIssuesPageAsync(PipelineJobTemplate template, int page)
        => LoadDrawerIssuesAsync(template, page);

    private async Task<string?> LoadDrawerLabelsCallbackAsync(PipelineJobTemplate template)
    {
        var ct = _issueDrawer.CancellationToken;
        try
        {
            var providerConfig = _cachedIssueProviders?.FirstOrDefault(p => p.Id == template.IssueProviderId);
            if (providerConfig == null) return null;
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            var labels = await provider.ListRepositoryLabelsAsync(ct);
            _issueDrawer.Labels = labels.ToList();
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _issueDrawer.Labels.Clear();
            return null;
        }
        catch
        {
            _issueDrawer.Labels.Clear();
            return null;
        }
    }

    public Task<string?> LoadDrawerLabelsAsync(PipelineJobTemplate template)
        => LoadDrawerLabelsCallbackAsync(template);

    public async Task CheckDrawerDependenciesAsync(
        PipelineJobTemplate template,
        Action? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var providerConfig = _cachedIssueProviders?.FirstOrDefault(p => p.Id == template.IssueProviderId);
        if (providerConfig == null) return;

        var issues = _issueDrawer.Items.ToList();
        var stateCache = new Dictionary<int, bool>();

        try
        {
            await using var provider = _providerFactory.CreateIssueProvider(providerConfig);
            foreach (var issue in issues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await _dependencyChecker.CheckAsync(
                    issue.Identifier, issue.Description, provider, stateCache, cancellationToken);
                DrawerReadiness[issue.Identifier] = result;
                onProgress?.Invoke();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Best-effort: partial results are still useful
        }
    }

    private async Task CheckDrawerDependenciesInBackgroundAsync(PipelineJobTemplate template, CancellationToken ct)
    {
        try
        {
            await CheckDrawerDependenciesAsync(template, null, ct);
        }
        catch (OperationCanceledException) { /* expected on drawer close */ }
    }

    public void ClearDrawerIssues()
    {
        _issueDrawer.Items.Clear();
        _issueDrawer.Page = 1;
        _issueDrawer.HasMore = false;
        DrawerReadiness.Clear();
        _issueDrawer.SelectedLabels.Clear();
    }

    // ── Cached provider context (set at open/switch time) ──

    private IReadOnlyList<ProviderConfig>? _cachedIssueProviders;
    private IReadOnlyList<ProviderConfig>? _cachedRepoProviders;

    // ── Dispatch ──

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchIssueAsync(
        IssueSummary issue,
        PipelineJobTemplate template,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject)
    {
        if (!issueProviders.Any(p => p.Id == template.IssueProviderId) || !repoProviders.Any(p => p.Id == template.RepoProviderId))
            return (false, "Template references providers that no longer exist.", null);

        var depProviderConfig = issueProviders.FirstOrDefault(p => p.Id == template.IssueProviderId);
        if (depProviderConfig != null)
        {
            await using var issueProvider = _providerFactory.CreateIssueProvider(depProviderConfig);
            var depResult = await _dependencyChecker.CheckAsync(issue.Identifier, issue.Description, issueProvider, new Dictionary<int, bool>(), CancellationToken.None);
            if (!depResult.IsReady)
                return (false, $"Cannot dispatch — issue is blocked by open dependencies: {string.Join(", ", depResult.BlockedBy.Select(n => $"#{n}"))}", null);
        }

        return await DrawerDispatchHelper.DispatchWithOrchestrationAsync(
            _dispatchOrchestration,
            project => _dispatchOrchestration.PrepareDistributionRequestAsync(
                new ImplementationDispatchOrchestrationRequest
                {
                    IssueIdentifier = issue.Identifier,
                    IssueProviderId = template.IssueProviderId,
                    RepoProviderId = template.RepoProviderId,
                    BrainProviderId = template.BrainProviderId,
                    PipelineProviderId = template.PipelineProviderId,
                    InitiatedBy = DrawerDispatchHelper.ManualInitiator,
                    Project = project
                }, CancellationToken.None),
            parentProject ?? new PipelineProject { Id = "", Name = "Unknown" },
            "Could not dispatch — distribution failed.",
            $"⏳ Queued #{issue.Identifier} — the job controller will start an agent pod for it",
            $"✅ Dispatched #{issue.Identifier}");
    }

    // ── Drawer orchestration ──

    public async Task<string?> OpenIssueDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null)
    {
        var template = templates.FirstOrDefault(t => t.Id == templateId.Value);
        if (template == null) return null;
        await RefreshActiveIssuesAsync();
        return await _issueDrawer.OpenAsync(template, notifyStateChanged);
    }

    public void CloseIssueDrawer()
    {
        _issueDrawer.Close();
        DrawerReadiness.Clear();
    }

    public Task<string?> SwitchToIssueDrawerAsync(
        TemplateId templateId,
        IReadOnlyList<PipelineJobTemplate> templates,
        Func<Task>? notifyStateChanged = null)
    {
        return _issueDrawer.SwitchAsync(templateId, notifyStateChanged,
            () => _issueDrawer.Items.Count > 0,
            async (id, ns) =>
            {
                var template = templates.FirstOrDefault(t => t.Id == id);
                if (template == null) return null;
                await RefreshActiveIssuesAsync();
                return template;
            });
    }

    public async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchFromIssueDrawerAsync(
        IssueSummary issue,
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders,
        PipelineProject? parentProject)
    {
        // Cache providers so the DrawerStateService dispatch callback can reach them
        _cachedIssueProviders = issueProviders;
        _cachedRepoProviders = repoProviders;

        _issueDrawer.IsDispatching = true;
        try
        {
            if (_issueDrawer.Template == null)
                return (false, "No template selected. Please select a template first.", null);

            var (success, error, successMessage) = await DispatchIssueAsync(issue, _issueDrawer.Template, issueProviders, repoProviders, parentProject);
            if (success)
                _issueDrawer.Close();
            return (success, error, successMessage);
        }
        finally { _issueDrawer.IsDispatching = false; }
    }

    // ── Active issues ──

    public async Task RefreshActiveIssuesAsync()
    {
        ActiveIssues = await _workDistributor.GetActiveIssueIdentifiersAsync(CancellationToken.None);
    }

    public bool IsIssueActive(IssueIdentifier issueIdentifier, string issueProviderConfigId)
        => ActiveIssues.Contains((issueIdentifier, issueProviderConfigId));

    public Task<bool> IsIssueDistributedAsync(string issueIdentifier, string issueProviderConfigId)
        => _workDistributor.IsIssueDistributedAsync(issueIdentifier, issueProviderConfigId, CancellationToken.None);

    // ── Cross-drawer coordination ──

    public void Hide() => _issueDrawer.IsOpen = false;

    // ── Provider context injection (called by coordinator before open/switch) ──

    internal void SetProviderContext(
        IReadOnlyList<ProviderConfig> issueProviders,
        IReadOnlyList<ProviderConfig> repoProviders)
    {
        _cachedIssueProviders = issueProviders;
        _cachedRepoProviders = repoProviders;
    }

    // ── IDisposable ──

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _issueDrawer.Dispose();
        _disposed = true;
    }
}
