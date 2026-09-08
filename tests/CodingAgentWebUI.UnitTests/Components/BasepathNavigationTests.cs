using AwesomeAssertions;
using Bunit;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Components.Layout;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Components.Shared;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// Verifies that all in-app navigation links use relative paths (no leading "/") so
/// they resolve correctly when the app is served under a non-root base path via a
/// reverse proxy such as Rancher. See issue #2336.
///
/// Root-relative hrefs like <c>href="/attention"</c> bypass the &lt;base href&gt;
/// configured in App.razor and produce 404s under a proxy prefix. Relative paths like
/// <c>href="attention"</c> are resolved against the base href by the browser and always
/// work regardless of the proxy prefix.
/// </summary>
public class BasepathNavigationTests : BunitContext
{
    // ── Helper ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that no anchor element in the rendered output has a root-relative internal href.
    /// A root-relative href starts with "/" (but not "//"). External links ("https://...") are
    /// not root-relative and therefore not checked.
    /// </summary>
    // TODO: This helper splits markup on '\n' and scans per line. Any href attribute that shares
    // a line with a preceding href on a sibling element could theoretically be missed if the inner
    // IndexOf loop fails to advance correctly, though Blazor/bUnit consistently renders one
    // attribute per line in practice. More importantly, this helper only detects root-relative
    // paths in rendered <a href="..."> attributes — it does NOT catch root-relative paths passed
    // to Nav.NavigateTo(...) via click handlers. The OpenRun methods in Overview, Attention, and
    // Runs all use NavigateTo and are NOT covered by this helper. Consider adding NavigateTo-
    // interception tests (capture FakeNavigationManager.Uri after triggering a row click) to
    // close this structural gap.
    private static void AssertNoAbsoluteInternalHrefs(string markup, string componentName)
    {
        // Parse <a href="..."> values that start with "/" — these are broken under a proxy.
        // A simple string search for href="/ is sufficient because Blazor/bUnit renders canonical HTML.
        var lines = markup.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var absoluteHrefs = new List<string>();
        foreach (var line in lines)
        {
            // Find all href="..." in the line
            var idx = 0;
            while (true)
            {
                var start = line.IndexOf("href=\"", idx, StringComparison.Ordinal);
                if (start < 0) break;
                start += 6; // skip href="
                var end = line.IndexOf('"', start);
                if (end < 0) break;
                var href = line[start..end];
                // Root-relative: starts with "/" but not "//" (protocol-relative)
                if (href.StartsWith('/') && !href.StartsWith("//"))
                    absoluteHrefs.Add(href);
                idx = end + 1;
            }
        }

        absoluteHrefs.Should().BeEmpty(
            $"{componentName} must not render root-relative hrefs (found: {string.Join(", ", absoluteHrefs)}). " +
            "Root-relative paths like \"/attention\" bypass <base href> and break under a Rancher proxy. " +
            "Use relative paths without a leading '/'.");
    }

    private static PagedResult<PipelineRunSummary> EmptyHistory() => new()
    {
        Items = new List<PipelineRunSummary>(), Page = 1, PageSize = 100, HasMore = false
    };

    // ── Overview ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Overview renders "Manage", "View all →", and "All runs →" anchor links as well as
    /// attention-panel links. All must be relative (issue #2336: "Manage" and attention badge).
    /// </summary>
    // TODO: This test renders Overview with EmptyHistory() and CockpitState.AttentionCount == 0.
    // The "Needs attention" panel (with four href="attention" links) is only rendered when
    // AttentionCount > 0, so those links are NOT in the markup this test inspects. A regression
    // reverting any of those four links back to href="/attention" would pass this test undetected.
    // Consider adding a second Overview test that sets AttentionCount > 0 and populates run
    // history so the attention panel and recent-activity rows are rendered and checked.
    [Fact]
    public void Overview_DoesNotRenderAbsoluteInternalHrefs()
    {
        var mockRunHistory = new Mock<IPipelineApiRunHistoryClient>();
        mockRunHistory.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyHistory());

        var mockAgents = new Mock<IPipelineApiAgentClient>();
        mockAgents.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntry>());

        var mockWorkItems = new Mock<IPipelineApiWorkItemClient>();
        mockWorkItems.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto>());
        mockWorkItems.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActiveWorkItemDto>());

        Services.AddSingleton(mockRunHistory.Object);
        Services.AddSingleton(mockAgents.Object);
        Services.AddSingleton(mockWorkItems.Object);
        Services.AddSingleton<ILoopStatusService>(Mock.Of<ILoopStatusService>());
        Services.AddSingleton(new CockpitState());

        var cut = Render<Overview>();

        AssertNoAbsoluteInternalHrefs(cut.Markup, nameof(Overview));
    }

    // ── Work ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Work renders a "Browse &amp; dispatch" anchor. It must be relative (issue #2336).
    /// </summary>
    // TODO: Work.razor's changes are href-only (no NavigateTo calls), so AssertNoAbsoluteInternalHrefs
    // covers the fix completely. However, the helper is structurally blind to NavigateTo calls —
    // if Work.razor ever adds programmatic navigation, those calls will not be covered by this test.
    [Fact]
    public void Work_DoesNotRenderAbsoluteInternalHrefs()
    {
        var mockWorkItems = new Mock<IPipelineApiWorkItemClient>();
        mockWorkItems.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto>());
        mockWorkItems.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActiveWorkItemDto>());

        var mockConfigClient = new Mock<IPipelineApiConfigClient>();
        mockConfigClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        mockConfigClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        Services.AddSingleton(mockWorkItems.Object);
        Services.AddSingleton(mockConfigClient.Object);
        Services.AddSingleton(Mock.Of<IProviderFactory>());
        Services.AddSingleton(Mock.Of<IDependencyChecker>());
        Services.AddSingleton<BlockedIssuesService>();
        Services.AddSingleton(new CockpitState());

        var cut = Render<Work>();

        AssertNoAbsoluteInternalHrefs(cut.Markup, nameof(Work));
    }

    // ── Attention ────────────────────────────────────────────────────────────

    /// <summary>
    /// The Attention page must not render any root-relative hrefs (issue #2336).
    /// </summary>
    // TODO: This test only covers the empty-data rendering path (EmptyHistory()). The critical fix
    // in Attention.razor is OpenRun => Nav.NavigateTo($"runs/{runId}"), which is only reachable
    // when the list has items and a row is clicked. With an empty list, reverting OpenRun back to
    // $"/runs/{runId}" would not be caught by this test. AssertNoAbsoluteInternalHrefs also cannot
    // detect NavigateTo arguments (only rendered href attributes). Consider adding a test that
    // populates the run list, simulates a row click, and asserts the resulting NavigationManager URI
    // is relative (i.e. does not start with the proxy-root path).
    [Fact]
    public void Attention_DoesNotRenderAbsoluteInternalHrefs()
    {
        var mockRunHistory = new Mock<IPipelineApiRunHistoryClient>();
        mockRunHistory.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyHistory());

        var mockConfigClient = new Mock<IPipelineApiConfigClient>();
        mockConfigClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        mockConfigClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        Services.AddSingleton(mockRunHistory.Object);
        Services.AddSingleton(mockConfigClient.Object);
        Services.AddSingleton(Mock.Of<IProviderFactory>());
        Services.AddSingleton(Mock.Of<IDependencyChecker>());
        Services.AddSingleton<BlockedIssuesService>();
        Services.AddSingleton(new CockpitState());

        var cut = Render<Attention>();

        AssertNoAbsoluteInternalHrefs(cut.Markup, nameof(Attention));
    }

    // ── RunPage ──────────────────────────────────────────────────────────────

    /// <summary>
    /// RunPage renders a "← Runs" back link which must be relative (issue #2336).
    /// </summary>
    // TODO: This test passes RunId = "test-run-id", which is not a valid Guid. If RunPage
    // attempts Guid.Parse on the parameter before the mock is consulted, it will throw a
    // FormatException unrelated to the href assertion. Use a valid Guid string (e.g.
    // Guid.NewGuid().ToString()) to avoid fragile test failures on parser changes.
    // Additionally, GetRunAsync returns null here, so any run-detail navigation that is only
    // rendered when a run is loaded is not exercised — those branches remain untested.
    [Fact]
    public void RunPage_DoesNotRenderAbsoluteInternalHrefs()
    {
        var mockHub = new Mock<IAgentHubConnection>();
        mockHub.Setup(h => h.State).Returns(HubConnectionState.Disconnected);
        mockHub.Setup(h => h.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockHub.Setup(h => h.On(It.IsAny<string>(), It.IsAny<Action>())).Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.On<It.IsAnyType>(It.IsAny<string>(), It.IsAny<Action<It.IsAnyType>>())).Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.On<It.IsAnyType, It.IsAnyType>(It.IsAny<string>(), It.IsAny<Action<It.IsAnyType, It.IsAnyType>>())).Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.On<It.IsAnyType, It.IsAnyType, It.IsAnyType>(It.IsAny<string>(), It.IsAny<Action<It.IsAnyType, It.IsAnyType, It.IsAnyType>>())).Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockRunHistory = new Mock<IPipelineApiRunHistoryClient>();
        mockRunHistory.Setup(c => c.GetRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PipelineRunSummary?)null);
        mockRunHistory.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyHistory());

        var mockConfigClient = new Mock<IPipelineApiConfigClient>();
        mockConfigClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockConfigClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mockConfigClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        mockConfigClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        // IConfigurationStore: RunPage uses GetProviderConfigByIdAsync when a run is present.
        // Since GetRunAsync returns null, the run-level code is never reached — a loose mock suffices.
        var mockConfigStore = Mock.Of<IConfigurationStore>();

        var mockWorkItems = new Mock<IPipelineApiWorkItemClient>();
        mockWorkItems.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActiveWorkItemDto>());

        Services.AddSingleton(mockRunHistory.Object);
        Services.AddSingleton(mockWorkItems.Object);
        Services.AddSingleton(mockConfigClient.Object);
        Services.AddSingleton(mockConfigStore);
        Services.AddSingleton(mockHub.Object);
        Services.AddSingleton(new CockpitState());

        var cut = Render<RunPage>(ps =>
            ps.Add(p => p.RunId, "test-run-id"));

        AssertNoAbsoluteInternalHrefs(cut.Markup, nameof(RunPage));
    }

    // TODO: There is no test for Runs.razor. The diff changes both the "All runs →" anchor
    // (href="/runs" → href="runs") and the OpenRun NavigateTo call ($"/runs/{runId}" →
    // $"runs/{runId}"). Neither change is covered by the current test suite. A regression
    // reverting either change would pass all tests. Add:
    // 1. Runs_DoesNotRenderAbsoluteInternalHrefs — renders the Runs component and calls
    //    AssertNoAbsoluteInternalHrefs to cover the anchor.
    // 2. Runs_OpenRun_UsesRelativeNavigation — populates run list, triggers a row click, and
    //    asserts the FakeNavigationManager navigated to "runs/{id}" (no leading "/").

    // ── CockpitLayout — attention badge ──────────────────────────────────────

    /// <summary>
    /// The top-bar attention badge in CockpitLayout must use a relative href (issue #2336).
    /// </summary>
    [Fact]
    public void CockpitLayout_AttentionBadge_UsesRelativeHref()
    {
        var mockLogger = new Mock<ILogger>();

        var mockConfigClient = new Mock<IPipelineApiConfigClient>();
        mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>());
        mockConfigClient.Setup(s => s.GetKeyValueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        mockConfigClient.Setup(s => s.HasEnabledTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var mockRunHistory = new Mock<IPipelineApiRunHistoryClient>();
        mockRunHistory.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyHistory());

        Services.AddSingleton(mockConfigClient.Object);
        Services.AddSingleton(mockRunHistory.Object);
        Services.AddSingleton(new CockpitState());
        Services.AddSingleton<IFaroService>(Mock.Of<IFaroService>());
        Services.AddSingleton<NotificationService>();
        Services.AddSingleton(sp => new NotificationFaroBridge(
            sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<IFaroService>()));

        var emptyConfig = new ConfigurationBuilder().Build();
        var emptyServiceProvider = new ServiceCollection().BuildServiceProvider();
        Services.AddSingleton(new InfrastructureHealthService(
            emptyServiceProvider, emptyConfig, Mock.Of<IPipelineApiHealthClient>()));
        Services.AddSingleton<IAgentRegistryService>(new AgentRegistryService(mockLogger.Object));
        Services.AddSingleton(Mock.Of<IJSRuntime>());

        var cut = Render<CockpitLayout>();

        // The .cockpit-attention link must be relative — no leading "/"
        var attentionLinks = cut.FindAll("a.cockpit-attention");
        attentionLinks.Should().HaveCountGreaterThan(0,
            "CockpitLayout must render the .cockpit-attention link in the top-bar");

        foreach (var link in attentionLinks)
        {
            var href = link.GetAttribute("href") ?? "";
            href.Should().NotStartWith("/",
                $"cockpit-attention href='{href}' is root-relative; it breaks under Rancher proxy. " +
                "Use 'attention' (no leading '/').");
        }
    }

    // ── FirstRunBanner — settings link ────────────────────────────────────────

    /// <summary>
    /// The "Settings → Job Templates" link in FirstRunBanner must be relative (issue #2336).
    /// </summary>
    [Fact]
    public void FirstRunBanner_SettingsLink_UsesRelativeHref()
    {
        // No enabled templates — banner will be shown
        var configClient = new Mock<IPipelineApiConfigClient>();
        configClient.Setup(s => s.HasEnabledTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        configClient.Setup(s => s.GetKeyValueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        Services.AddSingleton(configClient.Object);

        // Current route is home so the banner is visible
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/");

        var cut = Render<FirstRunBanner>();

        // Pre-condition: banner must be visible for the href assertion to be meaningful
        cut.Markup.Should().Contain("No job templates configured",
            "pre-condition: banner must be visible");

        AssertNoAbsoluteInternalHrefs(cut.Markup, nameof(FirstRunBanner));
    }
}
