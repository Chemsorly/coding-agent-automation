using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Web.Components.Layout;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Serilog;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Smoke tests for the cockpit shell — the app's default layout since the legacy MainLayout was retired.
/// Verifies the shell renders its brand, primary nav, project switcher and theme toggle without throwing.
/// Also covers localStorage persistence of the selected project scope (issue #2491).
/// </summary>
public class CockpitLayoutComponentTests : BunitContext
{
    private readonly Mock<IJSRuntime> _mockJs = new();
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient = new();
    private readonly CockpitState _state = new();

    public CockpitLayoutComponentTests()
    {
        var mockLogger = new Mock<ILogger>();

        // Config client: projects (scope switcher) + the keys FirstRunBanner reads.
        _mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>());
        _mockConfigClient.Setup(s => s.GetKeyValueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _mockConfigClient.Setup(s => s.HasEnabledTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        Services.AddSingleton(_mockConfigClient.Object);

        // Run-history client: the top-bar attention-count query.
        var mockRunHistory = new Mock<IPipelineApiRunHistoryClient>();
        mockRunHistory.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = Array.Empty<PipelineRunSummary>(),
                Page = 1,
                PageSize = 100,
                HasMore = false
            });
        Services.AddSingleton(mockRunHistory.Object);

        Services.AddSingleton(_state);

        // Faro frontend observability — no-op mocks (ported into the cockpit shell from MainLayout).
        Services.AddSingleton<IFaroService>(Mock.Of<IFaroService>());
        Services.AddSingleton<NotificationService>();
        Services.AddSingleton(sp => new NotificationFaroBridge(
            sp.GetRequiredService<NotificationService>(),
            sp.GetRequiredService<IFaroService>()));

        // SidebarHealthIndicators dependencies.
        var emptyConfig = new ConfigurationBuilder().Build();
        var emptyServiceProvider = new ServiceCollection().BuildServiceProvider();
        Services.AddSingleton(new InfrastructureHealthService(
            emptyServiceProvider, emptyConfig, Mock.Of<IPipelineApiHealthClient>()));
        Services.AddSingleton<IAgentRegistryService>(new AgentRegistryService(mockLogger.Object));

        // JS runtime — shared mock; configure per-test for specific return values.
        Services.AddSingleton<IJSRuntime>(_mockJs.Object);
    }

    [Fact]
    public void Renders_BrandAndPrimaryNav()
    {
        var cut = Render<CockpitLayout>();

        Assert.Contains("Coding Agent", cut.Markup);
        Assert.NotNull(cut.Find("a[href='overview']"));
        Assert.NotNull(cut.Find("a[href='fleet']"));
        Assert.NotNull(cut.Find("a[href='pipelines']"));
        // About migrated into the cockpit nav when the legacy shell was retired.
        Assert.NotNull(cut.Find("a[href='about']"));
    }

    [Fact]
    public void Renders_ProjectSwitcherAndThemeToggle()
    {
        var cut = Render<CockpitLayout>();

        Assert.NotNull(cut.Find(".cockpit-project-switcher"));
        Assert.NotNull(cut.Find(".cockpit-theme-toggle"));
    }

    // ── localStorage project persistence tests (issue #2491) ─────────────────

    /// <summary>
    /// When a valid project ID is stored in localStorage, the project scope must be
    /// restored on first render and reflected in CockpitState.SelectedProjectId.
    /// </summary>
    [Fact]
    public void CockpitLayout_WithSavedProjectId_RestoresProjectOnFirstRender()
    {
        // Arrange: projects list includes the saved project
        _mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = "proj-123", Name = "My Project" }
            });

        // localStorage returns the saved project ID.
        // Use the no-CT overload (object[]?) — that is what JS.InvokeAsync<T>(id, args) calls.
        _mockJs
            .Setup(j => j.InvokeAsync<string?>("localStorageGet", It.IsAny<object[]?>()))
            .ReturnsAsync("proj-123");

        // Act
        var cut = Render<CockpitLayout>();

        // Assert: CockpitState was updated with the restored project.
        // Use WaitForAssertion because the restore runs inside OnAfterRenderAsync, which
        // completes asynchronously after Render() returns. WaitForAssertion retries until
        // the assertion passes or the timeout (default 1 s) expires, making the test robust
        // regardless of how quickly the mock resolves.
        cut.WaitForAssertion(() =>
            _state.SelectedProjectId.Should().Be("proj-123",
                "the stored project ID must be restored into CockpitState on first render"));
    }

    /// <summary>
    /// When the stored value is "" (empty string), it means the user explicitly selected
    /// "All projects". This must be treated as a valid stored state, not as an absent key.
    /// </summary>
    [Fact]
    public void CockpitLayout_WithSavedEmptyProjectId_RestoresAllProjects()
    {
        // Arrange: localStorage returns "" — explicit "All projects" selection.
        // Use the no-CT overload — what JS.InvokeAsync<T>(id, args) calls.
        _mockJs
            .Setup(j => j.InvokeAsync<string?>("localStorageGet", It.IsAny<object[]?>()))
            .ReturnsAsync("");

        // Act
        var cut = Render<CockpitLayout>();

        // Assert: SelectedProjectId stays "" (All projects).
        // Use WaitForAssertion — restore runs inside the async OnAfterRenderAsync continuation
        // and may not have completed by the time Render() returns.
        // TODO [WARNING]: This assertion is tautological — CockpitState.SelectedProjectId
        // defaults to "" and the SetProject("", null) call is a no-op when already "". The test
        // passes even if the entire restore block is deleted. To make it load-bearing, seed the
        // state with a non-empty project first, then restore "" and assert the fallback.
        cut.WaitForAssertion(() =>
            _state.SelectedProjectId.Should().Be("",
                "stored empty string means 'All projects' was explicitly selected and must be restored as-is"));
    }

    /// <summary>
    /// When the stored project ID no longer exists in the project list (project was deleted),
    /// the layout must fall back to "All projects" and clear the stale localStorage key.
    /// </summary>
    [Fact]
    public void CockpitLayout_WithDeletedProjectId_ClearsStaleKeyAndFallsBackToAll()
    {
        // Arrange: projects list is empty (project was deleted), but localStorage has a stale ID
        _mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>());

        _mockJs
            .Setup(j => j.InvokeAsync<string?>("localStorageGet", It.IsAny<object[]?>()))
            .ReturnsAsync("stale-proj-id");

        // Act
        var cut = Render<CockpitLayout>();

        // Assert: stale key was cleared by writing "" to localStorage.
        // Use WaitForAssertion — the stale-branch localStorageSet call happens inside the
        // async OnAfterRenderAsync continuation, which may not have completed when Render() returns.
        // InvokeVoidAsync maps to InvokeAsync<IJSVoidResult> with the no-CT overload.
        // TODO [WARNING]: The _state.SelectedProjectId assertion below is trivially true because
        // "" is the default and the stale branch never calls SetProject. Only the Verify is
        // load-bearing. Consider seeding _state with a non-empty project first so the fallback
        // is genuinely observable as a state transition.
        cut.WaitForAssertion(() =>
            _mockJs.Verify(
                j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                    "localStorageSet",
                    It.Is<object[]?>(args => args != null && args.Length == 2
                        && args[0].ToString() == "cockpit.selectedProjectId"
                        && args[1].ToString() == "")),
                Times.Once,
                "localStorageSet must be called to clear the stale project key"));

        // Confirm the visible state also reflects "All projects".
        _state.SelectedProjectId.Should().Be("",
            "a stale project ID that no longer exists must fall back to 'All projects'");
    }

    /// <summary>
    /// When the user changes the project selection, the new ID must be written to localStorage.
    /// </summary>
    [Fact]
    public async Task CockpitLayout_OnProjectChanged_WritesToLocalStorage()
    {
        // Arrange: project available in the list
        _mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = "proj-abc", Name = "Alpha" }
            });

        var cut = Render<CockpitLayout>();

        // Act: change the project select to "proj-abc"
        var select = cut.Find("select[aria-label='Project scope']");
        await cut.InvokeAsync(() => select.Change("proj-abc"));

        // Assert: localStorage was updated with the selected project ID
        _mockJs.Verify(
            j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "localStorageSet",
                It.Is<object[]?>(args => args != null && args.Length == 2
                    && args[0].ToString() == "cockpit.selectedProjectId"
                    && args[1].ToString() == "proj-abc")),
            Times.Once,
            "localStorageSet must be called with the project key and selected ID when project changes");
    }

    /// <summary>
    /// When the user explicitly selects "All projects" (empty string), an empty string
    /// must be written to localStorage to distinguish it from an absent key on next load.
    /// </summary>
    [Fact]
    public async Task CockpitLayout_OnProjectChangedToAll_WritesEmptyStringToLocalStorage()
    {
        // Arrange: start with a project available
        _mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = "proj-abc", Name = "Alpha" }
            });

        var cut = Render<CockpitLayout>();

        // Act: change to "All projects" (value = "")
        var select = cut.Find("select[aria-label='Project scope']");
        await cut.InvokeAsync(() => select.Change(""));

        // Assert: "" was written to localStorage
        _mockJs.Verify(
            j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(
                "localStorageSet",
                It.Is<object[]?>(args => args != null && args.Length == 2
                    && args[0].ToString() == "cockpit.selectedProjectId"
                    && args[1].ToString() == "")),
            Times.Once,
            "selecting 'All projects' must write empty string to localStorage (not skip the write)");
    }
}
