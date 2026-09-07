using Bunit;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Components.Layout;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Components.Shared;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// Verifies that all deep-link hrefs rendered by cockpit components use relative paths
/// (no leading "/"), so they resolve correctly when the app is served under a Rancher
/// reverse-proxy sub-path. See issue #2336.
///
/// The rule: any <a> element that targets an in-app route must use a relative href
/// (e.g. "pipelines", "attention", "runs") rather than an absolute path ("/pipelines").
/// Relative hrefs are resolved against the HTML <base href>, which is configurable via
/// orchestrator.env.basePath in values.yaml.
/// </summary>
// TODO: [WARNING] The following changed lines are not fully covered by component tests in this file:
//   - Attention.razor:205 — OpenRun uses Nav.NavigateTo(Nav.BaseUri + $"runs/{runId}") (programmatic navigation)
//   - Runs.razor:243     — OpenRun uses Nav.NavigateTo(Nav.BaseUri + $"runs/{runId}") (programmatic navigation)
//   - RunPage.razor      — href="runs" back-link
// A revert of any of these to a root-relative path would not be caught by the current test suite.
// Add component render tests that: (a) verify RunPage renders href="runs" for the back link,
// and (b) verify Attention/Runs OpenRun calls NavigateTo with an absolute URL derived from Nav.BaseUri.
public class BasePathNavigationTests : BunitContext
{
    // ── Shared mocks ──────────────────────────────────────────────────────────

    private static Mock<IPipelineApiRunHistoryClient> EmptyRunHistoryMock()
    {
        var mock = new Mock<IPipelineApiRunHistoryClient>();
        mock.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = Array.Empty<PipelineRunSummary>(),
                Page = 1, PageSize = 50, HasMore = false
            });
        return mock;
    }

    private static Mock<IPipelineApiAgentClient> EmptyAgentClientMock()
    {
        var mock = new Mock<IPipelineApiAgentClient>();
        mock.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentEntry>());
        return mock;
    }

    private static Mock<IPipelineApiWorkItemClient> EmptyWorkItemsMock()
    {
        var mock = new Mock<IPipelineApiWorkItemClient>();
        mock.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingWorkItemDto>());
        mock.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ActiveWorkItemDto>());
        return mock;
    }

    // ── Overview: "Manage" button and attention badge links ───────────────────

    private void RegisterOverviewDeps()
    {
        var loopMock = new Mock<ILoopStatusService>();
        loopMock.SetupGet(s => s.IsLoopActive).Returns(false);
        loopMock.SetupGet(s => s.IsSchedulerUnreachable).Returns(false);
        loopMock.SetupGet(s => s.IsCircuitBroken).Returns(false);
        Services.AddSingleton(loopMock.Object);
        Services.AddSingleton(EmptyRunHistoryMock().Object);
        Services.AddSingleton(EmptyAgentClientMock().Object);
        Services.AddSingleton(EmptyWorkItemsMock().Object);
        Services.AddSingleton(new CockpitState());
    }

    /// <summary>
    /// Returns a run-history mock that includes one failed run so that
    /// _attnFailed > 0 and the "Needs attention" card is rendered by Overview.
    /// Without at least one attention-triggering run the card is conditionally
    /// hidden and the attention hrefs cannot be exercised.
    /// </summary>
    private static Mock<IPipelineApiRunHistoryClient> RunHistoryWithOneFailedRunMock()
    {
        var mock = new Mock<IPipelineApiRunHistoryClient>();
        mock.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = new[]
                {
                    new PipelineRunSummary
                    {
                        RunId = "run-1",
                        IssueIdentifier = (IssueIdentifier)"org/repo#1",
                        IssueTitle = "Test issue",
                        FinalStep = PipelineStep.Failed,
                        RunType = PipelineRunType.Implementation
                    }
                },
                Page = 1, PageSize = 50, HasMore = false
            });
        return mock;
    }

    [Fact]
    public void Overview_ManageButton_UsesRelativeHref()
    {
        RegisterOverviewDeps();

        var cut = Render<Overview>();

        // The "Manage" button must link to "pipelines" (relative), not "/pipelines" (absolute).
        var manageLink = cut.FindAll("a[href]")
            .FirstOrDefault(a => a.TextContent.Contains("Manage"));
        Assert.NotNull(manageLink);
        Assert.Equal("pipelines", manageLink!.GetAttribute("href"));
    }

    [Fact]
    public void Overview_RunsLink_UsesRelativeHref()
    {
        RegisterOverviewDeps();

        var cut = Render<Overview>();

        // The "All runs →" link must be relative.
        var runsLink = cut.FindAll("a[href]")
            .FirstOrDefault(a => a.TextContent.Contains("All runs"));
        Assert.NotNull(runsLink);
        Assert.Equal("runs", runsLink!.GetAttribute("href"));
    }

    [Fact]
    public void Overview_AttentionBadgeLinks_UseRelativeHref()
    {
        // The "Needs attention" card is conditionally rendered only when at least one
        // attention counter is non-zero. Use a run history that includes a failed run
        // so _attnFailed > 0 and the card (with its href="attention" links) is rendered.
        // TODO: [WARNING] This test registers services inline rather than calling RegisterOverviewDeps()
        // because it needs a different run-history mock. Any new dependency added to RegisterOverviewDeps
        // won't be reflected here automatically, which can cause this test to silently diverge. Consider
        // refactoring so RegisterOverviewDeps() is called and only the run-history service is replaced.
        var loopMock = new Mock<ILoopStatusService>();
        loopMock.SetupGet(s => s.IsLoopActive).Returns(false);
        loopMock.SetupGet(s => s.IsSchedulerUnreachable).Returns(false);
        loopMock.SetupGet(s => s.IsCircuitBroken).Returns(false);
        Services.AddSingleton(loopMock.Object);
        Services.AddSingleton(RunHistoryWithOneFailedRunMock().Object);
        Services.AddSingleton(EmptyAgentClientMock().Object);
        Services.AddSingleton(EmptyWorkItemsMock().Object);
        Services.AddSingleton(new CockpitState());

        var cut = Render<Overview>();

        // The "Needs attention" section must be present.
        var attentionLinks = cut.FindAll("a[href='attention']").ToList();
        // TODO: [WARNING] The inner loop assertion below is trivially true: the CSS selector
        // "a[href='attention']" already guarantees href == "attention" before the assertion runs.
        // A genuine guard against root-relative regressions should instead find ALL links that
        // text-match attention targets and assert none have href="/attention". Consider also
        // asserting the exact expected count (3 stat-cards + 1 "View all →" header = 4 total).
        Assert.NotEmpty(attentionLinks);

        // Every in-app attention link must be relative ("attention"), not "/attention".
        foreach (var link in attentionLinks)
        {
            Assert.Equal("attention", link.GetAttribute("href"));
        }
    }

    // TODO: [WARNING] CockpitLayout attention-badge link (CockpitLayout.razor, was href="/attention",
    // now href="attention") is not covered by any test in this file. The badge is rendered by
    // CockpitLayout which is not rendered here. A revert of that change would not be caught.
    // Add a test that renders CockpitLayout (or a wrapper component that uses it) and asserts
    // the badge link uses href="attention".

    // ── Work: "Browse & dispatch" button ──────────────────────────────────────

    [Fact]
    public void Work_BrowseAndDispatchButton_UsesRelativeHref()
    {
        var configMock = new Mock<IPipelineApiConfigClient>();
        configMock.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        configMock.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        Services.AddSingleton(EmptyWorkItemsMock().Object);
        Services.AddSingleton(configMock.Object);
        Services.AddSingleton(Mock.Of<IProviderFactory>());
        Services.AddSingleton(Mock.Of<IDependencyChecker>());
        Services.AddSingleton<BlockedIssuesService>();
        Services.AddSingleton(new CockpitState());

        var cut = Render<Work>();

        // The "Browse & dispatch" link must link to "pipelines" (relative).
        var dispatchLink = cut.FindAll("a[href]")
            .FirstOrDefault(a => a.TextContent.Contains("Browse") && a.TextContent.Contains("dispatch"));
        Assert.NotNull(dispatchLink);
        Assert.Equal("pipelines", dispatchLink!.GetAttribute("href"));
    }

    // ── FirstRunBanner: settings link and proxy-safe route detection ──────────

    [Fact]
    public void FirstRunBanner_SettingsLink_UsesRelativeHref()
    {
        var configMock = new Mock<IPipelineApiConfigClient>();
        configMock.Setup(c => c.HasEnabledTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        configMock.Setup(c => c.GetKeyValueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        Services.AddSingleton(configMock.Object);

        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/");

        var cut = Render<FirstRunBanner>();

        // The settings link must use "settings" (relative), not "/settings" (absolute).
        var settingsLink = cut.FindAll("a[href]")
            .FirstOrDefault(a => a.TextContent.Contains("Settings"));
        Assert.NotNull(settingsLink);
        Assert.Equal("settings", settingsLink!.GetAttribute("href"));
    }

    [Fact]
    public void FirstRunBanner_IsHidden_WhenOnSettingsPage_RootPath()
    {
        // Verifies that the banner is suppressed when the current route is "settings" under
        // the standard root deployment (no proxy prefix). This is a sanity check for the
        // ToBaseRelativePath-based route detection.
        var configMock = new Mock<IPipelineApiConfigClient>();
        configMock.Setup(c => c.HasEnabledTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        configMock.Setup(c => c.GetKeyValueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        Services.AddSingleton(configMock.Object);

        // Navigate to settings — bUnit base is http://localhost/, so relative path is "settings".
        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/settings");

        var cut = Render<FirstRunBanner>();

        Assert.DoesNotContain("No job templates configured", cut.Markup);
    }

    /// <summary>
    /// Verifies that route detection uses <see cref="NavigationManager.ToBaseRelativePath"/> rather
    /// than <see cref="Uri.AbsolutePath"/>. With a proxy base of "/proxy/", the settings page URL is
    /// "http://host/proxy/settings". The old AbsolutePath implementation returned "/proxy/settings",
    /// which did NOT match — the banner would show incorrectly. The new implementation calls
    /// ToBaseRelativePath which strips the base URI ("http://localhost/proxy/") and returns
    /// "settings", correctly hiding the banner.
    ///
    /// Because bUnit's FakeNavigationManager fixes BaseUri at "http://localhost/" and does not allow
    /// overriding it, this scenario is verified with a <see cref="StubNavigationManager"/> whose
    /// BaseUri can be set to the proxy path.
    /// </summary>
    [Fact]
    public void FirstRunBanner_IsHidden_WhenOnSettingsPage_ProxyPath()
    {
        var configMock = new Mock<IPipelineApiConfigClient>();
        configMock.Setup(c => c.HasEnabledTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        configMock.Setup(c => c.GetKeyValueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        Services.AddSingleton(configMock.Object);

        // Register a stub NavigationManager with a proxy base URI, overriding bUnit's default.
        // Base: "http://localhost/proxy/" — simulates Rancher serving the app under /proxy/.
        // Current URI: "http://localhost/proxy/settings" — user is on the settings page.
        var proxyNav = new StubNavigationManager("http://localhost/proxy/", "http://localhost/proxy/settings");
        Services.AddSingleton<NavigationManager>(proxyNav);

        var cut = Render<FirstRunBanner>();

        // Banner must be hidden: ToBaseRelativePath strips "http://localhost/proxy/" from
        // "http://localhost/proxy/settings", returning "settings" which matches the route check.
        // Regressing to AbsolutePath would return "/proxy/settings" (≠ "settings") and the
        // banner would incorrectly appear — this test would then fail, catching the regression.
        Assert.DoesNotContain("No job templates configured", cut.Markup);
    }
}

/// <summary>
/// A minimal <see cref="NavigationManager"/> stub that allows arbitrary BaseUri and Uri values.
/// Used to test components under a reverse-proxy base path that bUnit's FakeNavigationManager
/// cannot simulate (its BaseUri is fixed at "http://localhost/").
/// </summary>
internal sealed class StubNavigationManager : NavigationManager
{
    public StubNavigationManager(string baseUri, string currentUri)
    {
        Initialize(baseUri, currentUri);
    }

    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        Uri = uri;
    }
}
