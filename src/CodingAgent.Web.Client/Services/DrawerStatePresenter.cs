using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Services;

/// <summary>
/// Aggregates the shared drawer-state accessor properties that are identical across
/// <see cref="AgentCodingPageService"/> and <see cref="RepositoryPageService"/>.
/// Constructed from a <see cref="DispatchDrawers"/> record and delegates directly to the
/// underlying <see cref="DrawerStateService{TItem}"/> instances.
/// </summary>
public class DrawerStatePresenter
{
    private readonly IIssueDrawerService _issueDrawerService;
    private readonly IPrReviewDrawerService _prReviewDrawerService;
    private readonly IEpicDrawerService _epicDrawerService;

    public DrawerStatePresenter(DispatchDrawers drawers)
    {
        ArgumentNullException.ThrowIfNull(drawers);
        _issueDrawerService = drawers.Issue;
        _prReviewDrawerService = drawers.PrReview;
        _epicDrawerService = drawers.Epic;
    }

    // ── IsOpen ──

    public bool IsIssueDrawerOpen => _issueDrawerService.DrawerState.IsOpen;
    public bool IsPrDrawerOpen => _prReviewDrawerService.DrawerState.IsOpen;
    public bool IsEpicDrawerOpen => _epicDrawerService.DrawerState.IsOpen;

    // ── Templates ──

    public PipelineJobTemplate? IssueDrawerTemplate => _issueDrawerService.DrawerState.Template;
    public PipelineJobTemplate? PrDrawerTemplate => _prReviewDrawerService.DrawerState.Template;
    public PipelineJobTemplate? EpicDrawerTemplate => _epicDrawerService.DrawerState.Template;

    // ── IsDispatching (get + set) ──

    public bool IssueDrawerDispatching
    {
        get => _issueDrawerService.DrawerState.IsDispatching;
        set => _issueDrawerService.DrawerState.IsDispatching = value;
    }

    public bool PrDrawerDispatching
    {
        get => _prReviewDrawerService.DrawerState.IsDispatching;
        set => _prReviewDrawerService.DrawerState.IsDispatching = value;
    }

    public bool EpicDrawerDispatching
    {
        get => _epicDrawerService.DrawerState.IsDispatching;
        set => _epicDrawerService.DrawerState.IsDispatching = value;
    }

    // ── Issue drawer items ──

    public List<IssueSummary> DrawerIssues => _issueDrawerService.DrawerState.Items;
    public int DrawerPage => _issueDrawerService.DrawerState.Page;
    public bool DrawerHasMore => _issueDrawerService.DrawerState.HasMore;
    public bool DrawerLoading => _issueDrawerService.DrawerState.Loading;
    public List<string> DrawerLabels => _issueDrawerService.DrawerState.Labels;
    public List<string> DrawerSelectedLabels => _issueDrawerService.DrawerState.SelectedLabels;

    /// <summary>Dependency-readiness map from <see cref="IIssueDrawerService.DrawerReadiness"/>.</summary>
    public Dictionary<string, DependencyCheckResult> DrawerReadiness => _issueDrawerService.DrawerReadiness;

    // ── PR drawer items ──

    public List<PullRequestSummary> PrDrawerPrs => _prReviewDrawerService.DrawerState.Items;
    public int PrDrawerPage => _prReviewDrawerService.DrawerState.Page;
    public bool PrDrawerHasMore => _prReviewDrawerService.DrawerState.HasMore;
    public bool PrDrawerLoading => _prReviewDrawerService.DrawerState.Loading;
    public List<string> PrDrawerLabels => _prReviewDrawerService.DrawerState.Labels;
    public List<string> PrDrawerSelectedLabels => _prReviewDrawerService.DrawerState.SelectedLabels;

    // ── Epic drawer items ──

    public List<IssueSummary> EpicDrawerIssues => _epicDrawerService.DrawerState.Items;
    public int EpicDrawerPage => _epicDrawerService.DrawerState.Page;
    public bool EpicDrawerHasMore => _epicDrawerService.DrawerState.HasMore;
    public bool EpicDrawerLoading => _epicDrawerService.DrawerState.Loading;
    public List<string> EpicDrawerLabels => _epicDrawerService.DrawerState.Labels;
    public List<string> EpicDrawerSelectedLabels => _epicDrawerService.DrawerState.SelectedLabels;
}
