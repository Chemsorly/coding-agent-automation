using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Services;

/// <summary>
/// Abstract base class that provides the 28 shared drawer-state accessor properties for page services
/// that own an <see cref="AgentCodingPageService"/> or <see cref="RepositoryPageService"/>.
/// Delegates every property to a <see cref="DrawerStatePresenter"/> constructed from the
/// <see cref="DispatchDrawers"/> passed to the derived class constructor.
/// </summary>
public abstract class DrawerStateAccessorBase
{
    private readonly DrawerStatePresenter _drawerPresenter;

    protected DrawerStateAccessorBase(DispatchDrawers drawers)
    {
        _drawerPresenter = new DrawerStatePresenter(drawers);
    }

    // ── IsOpen ──

    public bool IsIssueDrawerOpen => _drawerPresenter.IsIssueDrawerOpen;
    public bool IsPrDrawerOpen => _drawerPresenter.IsPrDrawerOpen;
    public bool IsEpicDrawerOpen => _drawerPresenter.IsEpicDrawerOpen;

    // ── Templates ──

    public PipelineJobTemplate? IssueDrawerTemplate => _drawerPresenter.IssueDrawerTemplate;
    public PipelineJobTemplate? PrDrawerTemplate => _drawerPresenter.PrDrawerTemplate;
    public PipelineJobTemplate? EpicDrawerTemplate => _drawerPresenter.EpicDrawerTemplate;

    // ── IsDispatching (get + set) ──

    public bool IssueDrawerDispatching
    {
        get => _drawerPresenter.IssueDrawerDispatching;
        set => _drawerPresenter.IssueDrawerDispatching = value;
    }

    public bool PrDrawerDispatching
    {
        get => _drawerPresenter.PrDrawerDispatching;
        set => _drawerPresenter.PrDrawerDispatching = value;
    }

    public bool EpicDrawerDispatching
    {
        get => _drawerPresenter.EpicDrawerDispatching;
        set => _drawerPresenter.EpicDrawerDispatching = value;
    }

    // ── Issue drawer items ──

    public List<IssueSummary> DrawerIssues => _drawerPresenter.DrawerIssues;
    public int DrawerPage => _drawerPresenter.DrawerPage;
    public bool DrawerHasMore => _drawerPresenter.DrawerHasMore;
    public bool DrawerLoading => _drawerPresenter.DrawerLoading;
    public List<string> DrawerLabels => _drawerPresenter.DrawerLabels;
    public List<string> DrawerSelectedLabels => _drawerPresenter.DrawerSelectedLabels;

    /// <summary>Dependency-readiness map forwarded from <see cref="IIssueDrawerService.DrawerReadiness"/>.</summary>
    public Dictionary<string, DependencyCheckResult> DrawerReadiness => _drawerPresenter.DrawerReadiness;

    // ── PR drawer items ──

    public List<PullRequestSummary> PrDrawerPrs => _drawerPresenter.PrDrawerPrs;
    public int PrDrawerPage => _drawerPresenter.PrDrawerPage;
    public bool PrDrawerHasMore => _drawerPresenter.PrDrawerHasMore;
    public bool PrDrawerLoading => _drawerPresenter.PrDrawerLoading;
    public List<string> PrDrawerLabels => _drawerPresenter.PrDrawerLabels;
    public List<string> PrDrawerSelectedLabels => _drawerPresenter.PrDrawerSelectedLabels;

    // ── Epic drawer items ──

    public List<IssueSummary> EpicDrawerIssues => _drawerPresenter.EpicDrawerIssues;
    public int EpicDrawerPage => _drawerPresenter.EpicDrawerPage;
    public bool EpicDrawerHasMore => _drawerPresenter.EpicDrawerHasMore;
    public bool EpicDrawerLoading => _drawerPresenter.EpicDrawerLoading;
    public List<string> EpicDrawerLabels => _drawerPresenter.EpicDrawerLabels;
    public List<string> EpicDrawerSelectedLabels => _drawerPresenter.EpicDrawerSelectedLabels;
}
