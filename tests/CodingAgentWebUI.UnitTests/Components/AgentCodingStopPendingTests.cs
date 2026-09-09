using Bunit;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit tests for the _stopPending behaviour added in issue #2369.
/// Verifies that:
/// - The Stop Loop button is disabled immediately on click while the stop is in-flight.
/// - An animated spinner element is visible in the toast while stopping.
/// - The button re-enables once IsLoopActive becomes false.
/// - The button re-enables immediately when the stop request fails (no permanent lock-out).
/// - A second click while the first is in flight does not send a duplicate request.
/// </summary>
public class AgentCodingStopPendingTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore = new();
    private readonly Mock<IProjectStore> _mockProjectStore = new();
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient = new();
    private readonly Mock<IProviderFactory> _mockFactory = new();
    private readonly Mock<IWorkDistributor> _mockWorkDistributor = new();
    private readonly Mock<ILoopStatusService> _mockLoopStatus = new();
    private readonly Mock<ISchedulerApiClient> _mockSchedulerClient = new();

    private static PipelineJobTemplate DefaultTemplate => new()
    {
        Id = "t-1",
        Name = "DotNet Repo",
        IssueProviderId = "ip-1",
        RepoProviderId = "rp-1",
        Enabled = true
    };

    public AgentCodingStopPendingTests()
    {
        var mockLogger = new Mock<ILogger>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        var pipelineService = TestOrchestrationFactory.CreateMinimal(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            historyService: mockHistoryService.Object);

        TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            historyService: mockHistoryService.Object);

        SetupStoreMocks();
        SetupProjectStoreMocks();
        SetupConfigClientMocks();

        // Default loop state: active so Stop button is visible
        _mockLoopStatus.SetupGet(l => l.IsLoopActive).Returns(true);
        _mockLoopStatus.SetupGet(l => l.IsCircuitBroken).Returns(false);
        _mockLoopStatus.SetupGet(l => l.StatusMessage).Returns("Running…");
        _mockLoopStatus.SetupGet(l => l.ValidationErrors).Returns(Array.Empty<string>());
        _mockLoopStatus.SetupGet(l => l.TemplateStatuses)
            .Returns(new Dictionary<string, ConfigStatusSnapshot>());
        _mockLoopStatus.SetupGet(l => l.IsSchedulerUnreachable).Returns(false);
        _mockLoopStatus.SetupGet(l => l.CurrentCycleTemplateIndex).Returns(0);
        _mockLoopStatus.SetupGet(l => l.CurrentCycleTemplateCount).Returns(1);
        _mockLoopStatus.SetupGet(l => l.ProcessedCount).Returns(0);
        _mockLoopStatus.SetupGet(l => l.FailedCount).Returns(0);

        // Default: StopLoop completes instantly (overridden per test)
        _mockSchedulerClient
            .Setup(c => c.StartLoopAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoopStartResultDto(true, null));
        _mockSchedulerClient
            .Setup(c => c.StopLoopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Services.AddSingleton(pipelineService);
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockFactory.Object);
        Services.AddSingleton<ILoopStatusService>(_mockLoopStatus.Object);
        Services.AddSingleton<ISchedulerApiClient>(_mockSchedulerClient.Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
        Services.AddSingleton(_mockProjectStore.Object);
        Services.AddSingleton<IPipelineApiConfigClient>(_mockConfigClient.Object);

        var registry = new AgentRegistryService(mockLogger.Object);
        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
        Services.AddSingleton(new OrchestratorRunService(mockLogger.Object));
        Services.AddSingleton<IWorkDistributor>(_mockWorkDistributor.Object);
        Services.AddSingleton<IDependencyChecker>(new DependencyChecker(mockLogger.Object));
        Services.AddSingleton<IDispatchOrchestrationService>(new Mock<IDispatchOrchestrationService>().Object);

        Services.AddScoped<IIssueDrawerService, IssueDrawerService>();
        Services.AddScoped<IPrReviewDrawerService, PrReviewDrawerService>();
        Services.AddScoped<IEpicDrawerService, EpicDrawerService>();
        Services.AddScoped<AgentCodingPageService>();
        Services.AddScoped<NotificationService>();
        Services.AddEmbeddedConsolidationDeps();
    }

    // ── Helper: invoke StopLoop() via reflection inside InvokeAsync ───────────

    private static async Task InvokeStopLoopAsync(IRenderedComponent<AgentCoding> cut)
    {
        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentCoding).GetMethod(
                "StopLoop",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            await (Task)method.Invoke(cut.Instance, null)!;
        });
    }

    private static bool GetStopPending(IRenderedComponent<AgentCoding> cut)
    {
        var field = typeof(AgentCoding).GetField(
            "_stopPending",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (bool)field.GetValue(cut.Instance)!;
    }

    // ── Test 1: Controls section Stop button is disabled while stop is in-flight ──

    [Fact]
    public async Task WhenStopLoopClicked_ControlsSectionStopButtonBecomesDisabled()
    {
        // Arrange: StopLoopAsync is slow — use a TCS so the method hangs in-flight
        var tcs = new TaskCompletionSource();
        _mockSchedulerClient
            .Setup(c => c.StopLoopAsync(It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var cut = Render<AgentCoding>();

        // Act: start the stop without awaiting the TCS
        // We fire-and-forget the whole StopLoop method and assert before TCS completes.
        // InvokeAsync with the reflection call will return after StateHasChanged() runs
        // but before the underlying tcs.Task resolves (since tcs is never completed here).
        // TODO: The fire-and-forget pattern here is fragile — the disabled attribute assertion
        // depends on Blazor re-rendering after StateHasChanged(), but bUnit does not guarantee
        // synchronous render flush for tasks spawned outside InvokeAsync. If a future bUnit or
        // Blazor version changes render scheduling this could become a false positive.
        // Consider awaiting InvokeStopLoopAsync directly with a non-completing TCS to make the
        // timing explicit and deterministic.
        _ = InvokeStopLoopAsync(cut);

        // Give the Blazor renderer one tick to process StateHasChanged()
        await cut.InvokeAsync(() => { });

        // Assert: _stopPending is true
        Assert.True(GetStopPending(cut), "_stopPending should be true while stop is in-flight");

        // The Loop Controls Stop button should be rendered with disabled attribute
        var stopButtons = cut.FindAll("button").Where(b => b.TextContent.Contains("Stop Loop")).ToList();
        Assert.NotEmpty(stopButtons);
        Assert.All(stopButtons, btn => Assert.True(
            btn.HasAttribute("disabled"),
            $"Stop Loop button should be disabled while stop is in-flight. Button text: '{btn.TextContent}'"));

        // Cleanup: allow TCS to complete so nothing leaks
        tcs.SetResult();
    }

    // ── Test 2: Toast Stop button is disabled while stop is in-flight ─────────

    [Fact]
    public async Task WhenStopLoopClicked_ToastStopButtonBecomesDisabled()
    {
        // Arrange: ensure toast is visible (IsLoopActive=true, _hideLoopToast=false)
        var tcs = new TaskCompletionSource();
        _mockSchedulerClient
            .Setup(c => c.StopLoopAsync(It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var cut = Render<AgentCoding>();

        // Act
        _ = InvokeStopLoopAsync(cut);
        await cut.InvokeAsync(() => { });

        // Assert: the toast bar's Stop Loop button has disabled attribute
        var loopStatusBar = cut.Find(".loop-status-bar");
        var toastStopBtn = loopStatusBar.QuerySelector("button.loop-stop-btn");
        Assert.NotNull(toastStopBtn);
        Assert.True(toastStopBtn.HasAttribute("disabled"),
            "Toast Stop Loop button should be disabled while stop is in-flight");

        tcs.SetResult();
    }

    // ── Test 3: Animated spinner shown in toast while stop is in-flight ───────

    [Fact]
    public async Task WhenStopLoopClicked_AnimatedSpinnerShownInToast()
    {
        // Arrange
        var tcs = new TaskCompletionSource();
        _mockSchedulerClient
            .Setup(c => c.StopLoopAsync(It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var cut = Render<AgentCoding>();

        // Act
        _ = InvokeStopLoopAsync(cut);
        await cut.InvokeAsync(() => { });

        // Assert: spinner element is visible in the loop-status-bar
        var loopStatusBar = cut.Find(".loop-status-bar");
        // TODO: The assertion below only checks that .loop-stop-spinner exists inside the toast bar;
        // it does not verify the element is inside .loop-status-text (not the button), nor that it is
        // not hidden via display:none. Tighten to loopStatusBar.Find(".loop-status-text .loop-stop-spinner")
        // if the spinner were ever accidentally moved or conditionally hidden.
        var spinner = loopStatusBar.QuerySelector(".loop-stop-spinner");
        Assert.NotNull(spinner);

        tcs.SetResult();
    }

    // ── Test 4: Button re-enables and spinner disappears once loop stops ───────

    [Fact]
    public async Task WhenLoopStopsAfterStopClick_ButtonReturnsToNormal()
    {
        // Arrange: stop call completes immediately
        _mockSchedulerClient
            .Setup(c => c.StopLoopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = Render<AgentCoding>();

        // Act: invoke stop
        await InvokeStopLoopAsync(cut);

        // Simulate poller reporting IsLoopActive=false
        _mockLoopStatus.SetupGet(l => l.IsLoopActive).Returns(false);
        _mockLoopStatus.SetupGet(l => l.StatusMessage).Returns("Loop stopped.");

        // Fire HandleStateChanged (simulating the OnChange event from the polling service)
        // TODO: Invoking HandleStateChanged via reflection bypasses the production InvokeAsync
        // wrapper, meaning the _stopPending mutation and StateHasChanged run on the test thread
        // rather than through Blazor's synchronization context. This makes the test unable to catch
        // a regression where the InvokeAsync wrapping is accidentally removed. A more faithful
        // approach would trigger the OnChange event on ILoopStatusService directly (simulating the
        // real polling path) rather than calling HandleStateChanged via reflection.
        await cut.InvokeAsync(() =>
        {
            var method = typeof(AgentCoding).GetMethod(
                "HandleStateChanged",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            method.Invoke(cut.Instance, null);
        });

        // Drain any async continuations spawned by HandleStateChanged
        await cut.InvokeAsync(() => { });

        // Assert: _stopPending cleared
        Assert.False(GetStopPending(cut), "_stopPending should be false once loop stops");

        // TODO: Strengthen the assertion below to also verify that the .loop-stop-spinner is gone
        // and that the Stop Loop button no longer has the disabled attribute — this would directly
        // confirm _stopPending rollback rather than relying on unrelated Start Loop markup appearing.
        // Start Loop button should now be shown (loop is inactive)
        Assert.Contains("Start Loop", cut.Markup);
    }

    // ── Test 5: Stop request failure clears _stopPending immediately ──────────

    [Fact]
    public async Task WhenStopLoopFails_StopPendingClearedAndErrorShown()
    {
        // Arrange: ISchedulerApiClient.StopLoopAsync throws — AgentCodingPageService
        // catches and returns (false, error) to the component
        _mockSchedulerClient
            .Setup(c => c.StopLoopAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("scheduler unavailable"));

        var cut = Render<AgentCoding>();

        // Act: stop request fails
        await InvokeStopLoopAsync(cut);

        // Assert: _stopPending is immediately cleared (stop never happened)
        Assert.False(GetStopPending(cut),
            "_stopPending must be cleared when the stop request fails, otherwise the button stays permanently disabled");

        // Assert: an error element is visible — check the rendered error container's presence
        // rather than a specific message string so the test does not break on error-message rewording.
        var errorElement = cut.Find(".status-error");
        Assert.NotNull(errorElement);
        Assert.False(string.IsNullOrWhiteSpace(errorElement.TextContent),
            "Error element must contain a non-empty message when the stop request fails");
    }

    // ── Test 6: No duplicate stop requests sent ───────────────────────────────

    [Fact]
    public async Task WhenStopLoopClickedTwice_OnlyOneRequestSentToScheduler()
    {
        // Arrange: slow stop so the second click arrives while the first is in-flight
        var tcs = new TaskCompletionSource();
        _mockSchedulerClient
            .Setup(c => c.StopLoopAsync(It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var cut = Render<AgentCoding>();

        // Act: start first stop (in-flight, not awaited)
        _ = InvokeStopLoopAsync(cut);
        await cut.InvokeAsync(() => { }); // let StateHasChanged() fire

        // Assert _stopPending is true and all buttons are disabled (UI guard)
        Assert.True(GetStopPending(cut));
        var stopButtons = cut.FindAll("button").Where(b => b.TextContent.Contains("Stop Loop")).ToList();
        Assert.All(stopButtons, btn => Assert.True(btn.HasAttribute("disabled"),
            "All Stop buttons must be disabled to prevent duplicate scheduler calls"));

        // Act: invoke StopLoop a second time via reflection (simulating a programmatic duplicate call
        // or a future keyboard-shortcut path that bypasses the disabled-attribute UI guard).
        // The method-level guard (`if (_stopPending) return`) must prevent a second scheduler call.
        // TODO: The second InvokeStopLoopAsync call is fire-and-forgotten here; there is no guarantee
        // that its synchronous portion (_stopPending check + early return) has completed before the
        // Verify assertion below runs. If task scheduling changes this Verify could pass trivially
        // before the second invocation enters the guard. Consider awaiting the second call explicitly
        // (since it returns immediately via the guard and does not block on tcs) to make the
        // sequencing deterministic.
        _ = InvokeStopLoopAsync(cut);
        await cut.InvokeAsync(() => { }); // drain continuations

        // Assert: scheduler was still called exactly once — the internal guard fired
        _mockSchedulerClient.Verify(c => c.StopLoopAsync(It.IsAny<CancellationToken>()), Times.Once,
            "StopLoop's internal _stopPending guard must prevent a second scheduler call");

        // Cleanup
        tcs.SetResult();
        await cut.InvokeAsync(() => { });
    }

    // ── Setup helpers ─────────────────────────────────────────────────────────

    private void SetupStoreMocks()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "GitHub Issues" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "GitHub Repo" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() });
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ReviewerConfiguration>());
        _mockStore.Setup(s => s.SavePipelineConfigAsync(It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void SetupProjectStoreMocks()
    {
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", Enabled = true, TemplateIds = new[] { "t-1" } }
            });
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { DefaultTemplate });
        _mockProjectStore.Setup(s => s.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProjectStore.Setup(s => s.DeleteTemplateAsync(It.IsAny<string>(), It.IsAny<TemplateId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockProjectStore.Setup(s => s.SaveProjectAsync(It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void SetupConfigClientMocks()
    {
        _mockConfigClient.Setup(c => c.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns<ProviderKind, CancellationToken>((kind, ct) => _mockStore.Object.LoadProviderConfigsAsync(kind, ct));
        _mockConfigClient.Setup(c => c.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns<ProviderKind, CancellationToken>((kind, ct) => _mockStore.Object.LoadProviderConfigsAsync(kind, ct));
        _mockConfigClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadPipelineConfigAsync(ct));
        _mockConfigClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockProjectStore.Object.LoadAllTemplatesAsync(ct));
        _mockConfigClient.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockProjectStore.Object.LoadProjectsAsync(ct));
        _mockConfigClient.Setup(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadQualityGateConfigsAsync(ct));
        _mockConfigClient.Setup(c => c.GetReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadReviewerConfigsAsync(ct));
        _mockConfigClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _mockStore.Object.LoadAgentProfilesAsync(ct));
        _mockConfigClient.Setup(c => c.SaveTemplateAsync(It.IsAny<string>(), It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockConfigClient.Setup(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }
}
