using AwesomeAssertions;
using Bunit;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// Additional bUnit component tests for AgentMonitoring covering branches not exercised by
/// AgentMonitoringComponentTests and AgentMonitoringPageComponentTests:
/// HandleOutputLines, HandleStepTransition, HandleRunCompleted (hub event handlers),
/// OpenRunDetail (hub connected path), DismissRunDetailModal, HandleModalKeyDown,
/// SelectAgent, ShowDisconnectConfirm, ForceDisconnect, DisposeAsync,
/// HistoryModal open/close, ReloadCompletedRunAsync exception path.
/// </summary>
public class AgentMonitoringAdditionalTests : BunitContext
{
    private static readonly string[] OneLine = ["line1"];
    private static readonly string[] DiffLine = ["different-run-line"];
    private static readonly string[] TwoLines = ["hello", "world"];
    private readonly Mock<IAgentHubConnection> _mockHub = new();
    private readonly Mock<IPipelineApiRunHistoryClient> _mockRunHistoryClient = new();
    private readonly Mock<IConsolidationService> _mockConsolidation = new();
    private readonly Mock<IWorkDistributor> _mockWorkDistributor = new();

    // Captured hub event handlers — set during On<> setup
    private Action<string, IReadOnlyList<string>>? _onOutputLines;
    private Action<string, PipelineStep, DateTimeOffset>? _onStepTransition;
    private Action<string, JobCompletionPayload>? _onRunCompleted;
    private Action<string, RunStateSnapshot>? _onRunStateSnapshot;
    // Captured Reconnected event handler
    private Func<string?, Task>? _reconnectedHandler;

    public AgentMonitoringAdditionalTests()
    {
        var mockLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockLogger.Object);

        var mockConfigClient = new Mock<IPipelineApiConfigClient>();
        mockConfigClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockConfigClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mockConfigClient.Setup(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        mockConfigClient.Setup(c => c.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var mockConfigStore = new Mock<IConfigurationStore>();
        mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        _mockRunHistoryClient.Setup(c => c.GetRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PipelineRunSummary?)null);
        _mockRunHistoryClient.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = Array.Empty<PipelineRunSummary>(), Page = 1, PageSize = 1000, HasMore = false
            });

        _mockConsolidation.Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ConsolidationRun>());

        // Hub starts Disconnected — individual tests can override to Connected
        _mockHub.SetupGet(h => h.State).Returns(HubConnectionState.Disconnected);
        _mockHub.Setup(h => h.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockHub.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _mockHub.Setup(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Capture On<> handlers for direct invocation in tests
        _mockHub.Setup(h => h.On<string, IReadOnlyList<string>>(
                HubMethodNames.OnOutputLines, It.IsAny<Action<string, IReadOnlyList<string>>>()))
            .Returns<string, Action<string, IReadOnlyList<string>>>((_, cb) =>
            {
                _onOutputLines = cb;
                return Mock.Of<IDisposable>();
            });
        _mockHub.Setup(h => h.On<string, PipelineStep, DateTimeOffset>(
                HubMethodNames.OnStepTransition, It.IsAny<Action<string, PipelineStep, DateTimeOffset>>()))
            .Returns<string, Action<string, PipelineStep, DateTimeOffset>>((_, cb) =>
            {
                _onStepTransition = cb;
                return Mock.Of<IDisposable>();
            });
        _mockHub.Setup(h => h.On<string, JobCompletionPayload>(
                HubMethodNames.OnRunCompleted, It.IsAny<Action<string, JobCompletionPayload>>()))
            .Returns<string, Action<string, JobCompletionPayload>>((_, cb) =>
            {
                _onRunCompleted = cb;
                return Mock.Of<IDisposable>();
            });

        // Capture OnRunStateSnapshot handler for testing snapshot-seeded sidebar behaviour
        _mockHub.Setup(h => h.On<string, RunStateSnapshot>(
                HubMethodNames.OnRunStateSnapshot, It.IsAny<Action<string, RunStateSnapshot>>()))
            .Returns<string, Action<string, RunStateSnapshot>>((_, cb) =>
            {
                _onRunStateSnapshot = cb;
                return Mock.Of<IDisposable>();
            });

        // Capture Reconnected event registration so tests can fire it
        _mockHub.SetupAdd(h => h.Reconnected += It.IsAny<Func<string?, Task>>())
            .Callback<Func<string?, Task>>(handler => _reconnectedHandler = handler);
        _mockHub.SetupRemove(h => h.Reconnected -= It.IsAny<Func<string?, Task>>());

        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
        Services.AddSingleton(new JobDeduplicationGuardService(registry, mockLogger.Object));
        Services.AddSingleton<IPipelineApiConfigClient>(mockConfigClient.Object);
        Services.AddSingleton<IConfigurationStore>(mockConfigStore.Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
        Services.AddSingleton(Mock.Of<ILabelService>());
        Services.AddSingleton<IConsolidationService>(_mockConsolidation.Object);
        Services.AddSingleton<IWorkDistributor>(_mockWorkDistributor.Object);
        Services.AddSingleton<IPendingWorkQuery>(Mock.Of<IPendingWorkQuery>(q =>
            q.GetPendingJobsAsync(It.IsAny<CancellationToken>()) ==
            Task.FromResult<IReadOnlyList<PendingJob>>(Array.Empty<PendingJob>())));
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        Services.AddSingleton<IAgentHubConnection>(_mockHub.Object);
        Services.AddSingleton<IPipelineApiRunHistoryClient>(_mockRunHistoryClient.Object);

        Services.AddScoped(sp => new AgentMonitoringPageServiceDependencies(
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<JobDeduplicationGuardService>(),
            sp.GetRequiredService<IPipelineApiConfigClient>(),
            sp.GetRequiredService<IConsolidationService>(),
            sp.GetRequiredService<IPendingWorkQuery>(),
            sp.GetRequiredService<IWorkDistributor>(),
            sp.GetRequiredService<IPipelineApiRunHistoryClient>()));
        Services.AddScoped<AgentMonitoringPageService>();
    }

    // ── Hub event handlers — filtering by _selectedRunId ────────────────────

    [Fact]
    public async Task HandleOutputLines_WhenNoModalOpen_IgnoresEvent()
    {
        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onOutputLines);

        // No run selected — _selectedRunId is null → handler must return early
        await cut.InvokeAsync(() => _onOutputLines!("some-run-id", OneLine));

        // Modal is not open — output lines not visible
        Assert.DoesNotContain("line1", cut.Markup);
    }

    [Fact]
    public async Task HandleOutputLines_WhenWrongRunId_IgnoresEvent()
    {
        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onOutputLines);

        // Open modal for run-A, but send event for run-B
        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, ["run-A"])!;
        });

        // Send output lines for a different run — should be ignored
        await cut.InvokeAsync(() => _onOutputLines!("run-B", DiffLine));

        // The output should not be added (run-B lines filtered out)
        var lines = GetField<List<string>>(cut.Instance, "_activeModalOutputLines");
        lines.Should().BeEmpty("lines from a different run ID must be filtered out");
    }

    [Fact]
    public async Task HandleOutputLines_WhenCorrectRunId_AppendsLines()
    {
        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onOutputLines);

        // Open modal for run-C
        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, ["run-C"])!;
        });

        // Send output lines for the correct run
        await cut.InvokeAsync(() => _onOutputLines!("run-C", TwoLines));

        var lines = GetField<List<string>>(cut.Instance, "_activeModalOutputLines");
        lines.Should().HaveCount(2);
        lines.Should().Contain("hello");
    }

    [Fact]
    public async Task HandleStepTransition_WhenCorrectRunId_UpdatesCurrentStep()
    {
        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onStepTransition);

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, ["run-D"])!;
        });

        await cut.InvokeAsync(() => _onStepTransition!("run-D", PipelineStep.GeneratingCode, DateTimeOffset.UtcNow));

        var step = GetField<PipelineStep?>(cut.Instance, "_activeModalCurrentStep");
        step.Should().Be(PipelineStep.GeneratingCode);
    }

    [Fact]
    public async Task HandleStepTransition_WhenWrongRunId_IgnoresEvent()
    {
        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onStepTransition);

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, ["run-E"])!;
        });

        await cut.InvokeAsync(() => _onStepTransition!("run-DIFFERENT", PipelineStep.Completed, DateTimeOffset.UtcNow));

        var step = GetField<PipelineStep?>(cut.Instance, "_activeModalCurrentStep");
        step.Should().BeNull("step transition from a different run must be ignored");
    }

    [Fact]
    public async Task HandleRunCompleted_WhenCorrectRunId_TriggersReload()
    {
        var runGuid = Guid.NewGuid();
        var completedSummary = new PipelineRunSummary
        {
            RunId = runGuid.ToString(),
            IssueIdentifier = "1",
            IssueTitle = "Completed Run",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedAtOffset = DateTimeOffset.UtcNow
        };

        _mockRunHistoryClient.Setup(c => c.GetRunAsync(runGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(completedSummary);

        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onRunCompleted);

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [runGuid.ToString()])!;
        });

        await cut.InvokeAsync(() => _onRunCompleted!(runGuid.ToString(), new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow }));

        // Wait for the async reload to complete
        await cut.InvokeAsync(() => { });

        _mockRunHistoryClient.Verify(
            c => c.GetRunAsync(runGuid, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task HandleRunCompleted_WhenWrongRunId_IgnoresEvent()
    {
        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onRunCompleted);

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, ["run-X"])!;
        });

        var anotherGuid = Guid.NewGuid();
        await cut.InvokeAsync(() => _onRunCompleted!(anotherGuid.ToString(), new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow }));
        await cut.InvokeAsync(() => { });

        // GetRunAsync should not have been called for the different run
        _mockRunHistoryClient.Verify(
            c => c.GetRunAsync(anotherGuid, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── OpenRunDetail ────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenRunDetail_SetsSelectedRunId()
    {
        var cut = Render<AgentMonitoring>();

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, ["my-run-id"])!;
        });

        var runId = GetField<string?>(cut.Instance, "_selectedRunId");
        runId.Should().Be("my-run-id");
    }

    [Fact]
    public async Task OpenRunDetail_ClearsOutputLines_AndCurrentStep()
    {
        var cut = Render<AgentMonitoring>();

        // Pre-populate state from a previous run
        await cut.InvokeAsync(() =>
        {
            GetField<List<string>>(cut.Instance, "_activeModalOutputLines").Add("stale line");
            SetField(cut.Instance, "_activeModalCurrentStep", (PipelineStep?)PipelineStep.Completed);
        });

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, ["new-run"])!;
        });

        GetField<List<string>>(cut.Instance, "_activeModalOutputLines").Should().BeEmpty("output must be reset on new modal open");
        GetField<PipelineStep?>(cut.Instance, "_activeModalCurrentStep").Should().BeNull("current step must be reset on new modal open");
    }

    [Fact]
    public async Task OpenRunDetail_WhenHubConnected_InvokesSubscribeToRun()
    {
        _mockHub.SetupGet(h => h.State).Returns(HubConnectionState.Connected);
        _mockHub.Setup(h => h.InvokeAsync(HubMethodNames.SubscribeToRun, It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = Render<AgentMonitoring>();

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, ["connected-run"])!;
        });

        _mockHub.Verify(
            h => h.InvokeAsync(HubMethodNames.SubscribeToRun, It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OpenRunDetail_WhenHubSubscribeThrows_DoesNotPropagate()
    {
        _mockHub.SetupGet(h => h.State).Returns(HubConnectionState.Connected);
        _mockHub.Setup(h => h.InvokeAsync(HubMethodNames.SubscribeToRun, It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub error"));

        var cut = Render<AgentMonitoring>();

        var act = async () =>
        {
            await cut.InvokeAsync(async () =>
            {
                var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                await (Task)method!.Invoke(cut.Instance, ["error-run"])!;
            });
        };

        await act.Should().NotThrowAsync("hub subscribe error must be caught inside OpenRunDetail");
    }

    [Fact]
    public async Task OpenRunDetail_LoadsRunSummaryFromApi_WhenRunIdIsValidGuid()
    {
        var runGuid = Guid.NewGuid();
        var summary = new PipelineRunSummary
        {
            RunId = runGuid.ToString(),
            IssueIdentifier = "1",
            IssueTitle = "API Run",
            FinalStep = PipelineStep.GeneratingCode,
            StartedAtOffset = DateTimeOffset.UtcNow
        };
        _mockRunHistoryClient.Setup(c => c.GetRunAsync(runGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var cut = Render<AgentMonitoring>();

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [runGuid.ToString()])!;
        });

        _mockRunHistoryClient.Verify(c => c.GetRunAsync(runGuid, It.IsAny<CancellationToken>()), Times.Once);
        GetField<PipelineRunSummary?>(cut.Instance, "_activeModalRun").Should().NotBeNull();
    }

    [Fact]
    public async Task OpenRunDetail_WhenApiThrows_DoesNotPropagate()
    {
        var runGuid = Guid.NewGuid();
        _mockRunHistoryClient.Setup(c => c.GetRunAsync(runGuid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network error"));

        var cut = Render<AgentMonitoring>();

        var act = async () =>
        {
            await cut.InvokeAsync(async () =>
            {
                var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                await (Task)method!.Invoke(cut.Instance, [runGuid.ToString()])!;
            });
        };

        await act.Should().NotThrowAsync("API error in OpenRunDetail must be caught");
    }

    // ── DismissRunDetailModal ────────────────────────────────────────────────

    [Fact]
    public async Task DismissRunDetailModal_ClearsModalState()
    {
        var cut = Render<AgentMonitoring>();

        // Open a modal first
        await cut.InvokeAsync(async () =>
        {
            var openMethod = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)openMethod!.Invoke(cut.Instance, ["dismiss-test-run"])!;
        });

        GetField<string?>(cut.Instance, "_selectedRunId").Should().Be("dismiss-test-run");

        // Dismiss
        await cut.InvokeAsync(async () =>
        {
            var dismissMethod = typeof(AgentMonitoring).GetMethod("DismissRunDetailModal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)dismissMethod!.Invoke(cut.Instance, null)!;
        });

        GetField<string?>(cut.Instance, "_selectedRunId").Should().BeNull();
        GetField<bool>(cut.Instance, "_showRunDetailModal").Should().BeFalse();
    }

    [Fact]
    public async Task DismissRunDetailModal_WhenHubConnected_InvokesUnsubscribeFromRun()
    {
        _mockHub.SetupGet(h => h.State).Returns(HubConnectionState.Connected);
        _mockHub.Setup(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = Render<AgentMonitoring>();

        // Open then dismiss
        await cut.InvokeAsync(async () =>
        {
            var openMethod = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)openMethod!.Invoke(cut.Instance, ["unsub-test-run"])!;
        });

        await cut.InvokeAsync(async () =>
        {
            var dismissMethod = typeof(AgentMonitoring).GetMethod("DismissRunDetailModal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)dismissMethod!.Invoke(cut.Instance, null)!;
        });

        _mockHub.Verify(
            h => h.InvokeAsync(HubMethodNames.UnsubscribeFromRun, It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DismissRunDetailModal_WhenNoModalOpen_DoesNotCallUnsubscribe()
    {
        _mockHub.SetupGet(h => h.State).Returns(HubConnectionState.Connected);

        var cut = Render<AgentMonitoring>();

        // Dismiss without opening first — _selectedRunId is null
        await cut.InvokeAsync(async () =>
        {
            var dismissMethod = typeof(AgentMonitoring).GetMethod("DismissRunDetailModal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)dismissMethod!.Invoke(cut.Instance, null)!;
        });

        _mockHub.Verify(
            h => h.InvokeAsync(HubMethodNames.UnsubscribeFromRun, It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── HandleModalKeyDown ────────────────────────────────────────────────────

    [Fact]
    public async Task HandleModalKeyDown_EscapeKey_DismissesModal()
    {
        var cut = Render<AgentMonitoring>();

        await cut.InvokeAsync(async () =>
        {
            var openMethod = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)openMethod!.Invoke(cut.Instance, ["escape-run"])!;
        });

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("HandleModalKeyDown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var args = new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" };
            await (Task)method!.Invoke(cut.Instance, [args])!;
        });

        GetField<bool>(cut.Instance, "_showRunDetailModal").Should().BeFalse();
    }

    [Fact]
    public async Task HandleModalKeyDown_NonEscapeKey_DoesNotDismissModal()
    {
        var cut = Render<AgentMonitoring>();

        await cut.InvokeAsync(async () =>
        {
            var openMethod = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)openMethod!.Invoke(cut.Instance, ["key-run"])!;
        });

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("HandleModalKeyDown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var args = new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" };
            await (Task)method!.Invoke(cut.Instance, [args])!;
        });

        GetField<bool>(cut.Instance, "_showRunDetailModal").Should().BeTrue("Enter key must not dismiss the modal");
    }

    // ── SelectAgent ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SelectAgent_WithNoActiveRunForAgent_DoesNothing()
    {
        var cut = Render<AgentMonitoring>();

        // No active runs — SelectAgent should silently no-op
        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("SelectAgent",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, ["agent-no-run"])!;
        });

        GetField<string?>(cut.Instance, "_selectedRunId").Should().BeNull();
    }

    // ── ShowDisconnectConfirm ─────────────────────────────────────────────────

    [Fact]
    public async Task ShowDisconnectConfirm_SetsFlag()
    {
        var cut = Render<AgentMonitoring>();

        await cut.InvokeAsync(() =>
        {
            var method = typeof(AgentMonitoring).GetMethod("ShowDisconnectConfirm",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(cut.Instance, null);
        });

        GetField<bool>(cut.Instance, "_showDisconnectConfirm").Should().BeTrue();
    }

    // ── ForceDisconnect ───────────────────────────────────────────────────────

    [Fact]
    public async Task ForceDisconnect_CallsPageServiceAndDismissesModal()
    {
        var cut = Render<AgentMonitoring>();
        var pageService = Services.GetRequiredService<AgentMonitoringPageService>();

        // Open modal first so DismissRunDetailModal has something to dismiss
        await cut.InvokeAsync(async () =>
        {
            var openMethod = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)openMethod!.Invoke(cut.Instance, ["force-disc-run"])!;
        });

        var agent = new AgentEntry { AgentId = "agent-disconnect", ConnectionId = "conn-disc", Hostname = "host", Labels = [], RegisteredAt = DateTimeOffset.UtcNow };
        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("ForceDisconnect",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [agent])!;
        });

        GetField<bool>(cut.Instance, "_showDisconnectConfirm").Should().BeFalse("flag should be cleared after ForceDisconnect");
        GetField<bool>(cut.Instance, "_showRunDetailModal").Should().BeFalse("modal should be dismissed after ForceDisconnect");
    }

    // ── OpenHistoryRunDetail / DismissHistoryDetailModal ──────────────────────

    [Fact]
    public async Task OpenHistoryRunDetail_SetsHistoryModalState()
    {
        var cut = Render<AgentMonitoring>();
        var summary = new PipelineRunSummary
        {
            RunId = "hist-run-1",
            IssueIdentifier = "42",
            IssueTitle = "History Run",
            FinalStep = PipelineStep.Failed,
            StartedAtOffset = DateTimeOffset.UtcNow
        };

        await cut.InvokeAsync(() =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenHistoryRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(cut.Instance, [summary]);
        });

        GetField<bool>(cut.Instance, "_showHistoryDetailModal").Should().BeTrue();
        GetField<PipelineRunSummary?>(cut.Instance, "_selectedHistoryRun").Should().NotBeNull();
    }

    [Fact]
    public async Task DismissHistoryDetailModal_ClearsHistoryModalState()
    {
        var cut = Render<AgentMonitoring>();
        var summary = new PipelineRunSummary
        {
            RunId = "hist-run-2",
            IssueIdentifier = "43",
            IssueTitle = "History",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow
        };

        await cut.InvokeAsync(() =>
        {
            var openMethod = typeof(AgentMonitoring).GetMethod("OpenHistoryRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            openMethod?.Invoke(cut.Instance, [summary]);
        });

        await cut.InvokeAsync(() =>
        {
            var dismissMethod = typeof(AgentMonitoring).GetMethod("DismissHistoryDetailModal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            dismissMethod?.Invoke(cut.Instance, null);
        });

        GetField<bool>(cut.Instance, "_showHistoryDetailModal").Should().BeFalse();
        GetField<PipelineRunSummary?>(cut.Instance, "_selectedHistoryRun").Should().BeNull();
    }

    // ── HandleRemoveFromQueue adapter ─────────────────────────────────────────

    [Fact]
    public async Task HandleRemoveFromQueue_DelegatesToRemoveFromQueue()
    {
        // Use a mock IPendingWorkQuery that directly returns the job — avoids timing issues
        // with the JobDeduplicationGuardService + Timer + InvokeAsync chain
        var job = new PendingJob
        {
            IssueIdentifier = "org/repo#300",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test"
        };

        var pendingQueryMock = new Mock<IPendingWorkQuery>();
        pendingQueryMock.SetupSequence(q => q.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingJob> { job })   // First call — has job
            .ReturnsAsync(new List<PendingJob>());         // After remove — empty

        Services.AddSingleton<IPendingWorkQuery>(pendingQueryMock.Object);

        var cut = Render<AgentMonitoring>();

        // Wait for initial render to show the job
        cut.WaitForAssertion(() => Assert.Contains("org/repo#300", cut.Markup),
            timeout: TimeSpan.FromSeconds(10));

        // Invoke HandleRemoveFromQueue directly
        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("HandleRemoveFromQueue",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [("org/repo#300", "ip-1")])!;
        });

        // Verify the component rendered without exceptions after RemoveFromQueueAsync.
        Assert.NotNull(cut.Markup);
    }

    // ── EnableAgent / DisableAgent ────────────────────────────────────────────

    [Fact]
    public void EnableAgent_SetsAgentEnabled()
    {
        var agent = new AgentEntry
        {
            AgentId = "agent-1",
            ConnectionId = "conn-1",
            Hostname = "host-1",
            Labels = Array.Empty<string>(),
            RegisteredAt = DateTimeOffset.UtcNow,
            Disabled = true
        };

        AgentMonitoringPageService.EnableAgent(agent);

        agent.Disabled.Should().BeFalse();
    }

    [Fact]
    public void DisableAgent_SetsAgentDisabled()
    {
        var agent = new AgentEntry
        {
            AgentId = "agent-1",
            ConnectionId = "conn-1",
            Hostname = "host-1",
            Labels = Array.Empty<string>(),
            RegisteredAt = DateTimeOffset.UtcNow,
            Disabled = false
        };

        AgentMonitoringPageService.DisableAgent(agent);

        agent.Disabled.Should().BeTrue();
    }

    // ── DisposeAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_SetsDisposedFlag()
    {
        var cut = Render<AgentMonitoring>();

        await cut.Instance.DisposeAsync();

        GetField<bool>(cut.Instance, "_disposed").Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_DisposesHubSubscriptions()
    {
        var sub1 = new Mock<IDisposable>();
        var sub2 = new Mock<IDisposable>();
        var sub3 = new Mock<IDisposable>();

        // Override hub On<> to return trackable disposables
        var subs = new[] { sub1.Object, sub2.Object, sub3.Object };
        _mockHub.Setup(h => h.On<string, IReadOnlyList<string>>(It.IsAny<string>(), It.IsAny<Action<string, IReadOnlyList<string>>>()))
            .Returns(subs[0]);
        _mockHub.Setup(h => h.On<string, PipelineStep, DateTimeOffset>(It.IsAny<string>(), It.IsAny<Action<string, PipelineStep, DateTimeOffset>>()))
            .Returns(subs[1]);
        _mockHub.Setup(h => h.On<string, JobCompletionPayload>(It.IsAny<string>(), It.IsAny<Action<string, JobCompletionPayload>>()))
            .Returns(subs[2]);

        var cut = Render<AgentMonitoring>();
        await cut.Instance.DisposeAsync();

        sub1.Verify(d => d.Dispose(), Times.Once);
        sub2.Verify(d => d.Dispose(), Times.Once);
        sub3.Verify(d => d.Dispose(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_WhenModalOpenAndHubConnected_UnsubscribesFromRun()
    {
        _mockHub.SetupGet(h => h.State).Returns(HubConnectionState.Connected);
        _mockHub.Setup(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = Render<AgentMonitoring>();

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, ["open-on-dispose-run"])!;
        });

        await cut.Instance.DisposeAsync();

        _mockHub.Verify(
            h => h.InvokeAsync(HubMethodNames.UnsubscribeFromRun, It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "DisposeAsync must unsubscribe from the active run group when a modal is open");
    }

    [Fact]
    public async Task DisposeAsync_WhenHubUnsubscribeThrows_DoesNotPropagate()
    {
        _mockHub.SetupGet(h => h.State).Returns(HubConnectionState.Connected);
        _mockHub.Setup(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub disconnected"));

        var cut = Render<AgentMonitoring>();

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, ["throw-on-unsub-run"])!;
        });

        var act = () => cut.Instance.DisposeAsync().AsTask();
        await act.Should().NotThrowAsync("hub exception in DisposeAsync must be swallowed");
    }

    // ── ReloadCompletedRunAsync — API exception path ──────────────────────────

    [Fact]
    public async Task ReloadCompletedRunAsync_WhenApiThrows_DoesNotPropagate()
    {
        var runGuid = Guid.NewGuid();
        _mockRunHistoryClient.Setup(c => c.GetRunAsync(runGuid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network error"));

        var cut = Render<AgentMonitoring>();

        // Open a modal so _selectedRunId matches
        await cut.InvokeAsync(async () =>
        {
            var openMethod = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)openMethod!.Invoke(cut.Instance, [runGuid.ToString()])!;
        });

        // Simulate a run-completed event → calls ReloadCompletedRunAsync which calls GetRunAsync
        Assert.NotNull(_onRunCompleted);
        await cut.InvokeAsync(() => _onRunCompleted!(runGuid.ToString(), new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow }));
        await cut.InvokeAsync(() => { }); // flush async continuation

        // Component must still be alive — no exception propagated
        Assert.NotNull(cut.Markup);
    }

    // ── RunStateSnapshot hub event handling ───────────────────────────────────

    [Fact]
    public async Task HandleRunStateSnapshot_WhenJobIdMatches_SeedsActiveModalRunModel()
    {
        var runId = "snapshot-run-1";
        var snapshot = new RunStateSnapshot
        {
            CurrentStep = PipelineStep.GeneratingCode,
            HighWaterMark = PipelineStep.GeneratingCode,
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Snapshot Test Issue",
            IssueLabels = ["agent:next", "dotnet"],
            StartedAtOffset = DateTimeOffset.UtcNow,
        };

        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onRunStateSnapshot);

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [runId])!;
        });

        await cut.InvokeAsync(() => _onRunStateSnapshot!(runId, snapshot));

        var runModel = GetField<PipelineRun?>(cut.Instance, "_activeModalRunModel");
        runModel.Should().NotBeNull("snapshot should seed the sidebar view model");
        runModel!.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
        runModel.HighWaterMark.Should().Be(PipelineStep.GeneratingCode);
        // TODO [WARNING]: Weak assertion — only checks Contains on one entry out of two. A stronger assertion
        // would be BeEquivalentTo(["agent:next", "dotnet"]) to catch partial-copy bugs in ApplySnapshotToRunModel.
        runModel.IssueLabels.Should().Contain("agent:next");
    }

    [Fact]
    public async Task HandleRunStateSnapshot_WhenJobIdDoesNotMatch_IsIgnored()
    {
        var runId = "snapshot-run-2";
        var snapshot = new RunStateSnapshot
        {
            CurrentStep = PipelineStep.AnalyzingCode,
            HighWaterMark = PipelineStep.AnalyzingCode,
            IssueIdentifier = "org/repo#99",
        };

        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onRunStateSnapshot);

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [runId])!;
        });

        // Fire snapshot for a DIFFERENT run
        await cut.InvokeAsync(() => _onRunStateSnapshot!("different-run", snapshot));

        // TODO [WARNING]: This assertion is satisfied before the snapshot fires because OpenRunDetail does
        // not receive a RunStateSnapshot (no mock returns one), so the model was always going to be null.
        // The test would pass even if the job-ID guard in HandleRunStateSnapshot were entirely removed.
        // Fix: first seed the model with a matching snapshot, then fire the mismatched one, and assert
        // the model is unchanged — not simply absent.
        var runModel = GetField<PipelineRun?>(cut.Instance, "_activeModalRunModel");
        runModel.Should().BeNull("snapshot for a different run must not seed the model");
    }

    [Fact]
    public async Task HandleRunStateSnapshot_WhenSnapshotArrivesBeforeApiResponse_ConstructsRunModelFromSnapshot()
    {
        // Simulate the race: snapshot fires before GetRunAsync returns.
        // _activeModalRun is null when snapshot arrives.
        var runId = "snapshot-race-run";
        var snapshot = new RunStateSnapshot
        {
            CurrentStep = PipelineStep.VerifyingBaseline,
            HighWaterMark = PipelineStep.VerifyingBaseline,
            IssueIdentifier = "org/repo#100",
            IssueTitle = "Race Test",
        };

        // Make GetRunAsync return null (simulating active run not yet in history)
        _mockRunHistoryClient.Setup(c => c.GetRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PipelineRunSummary?)null);

        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onRunStateSnapshot);

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [runId])!;
        });

        // Fire snapshot immediately (before/during API call)
        await cut.InvokeAsync(() => _onRunStateSnapshot!(runId, snapshot));

        // TODO [WARNING]: This test does not actually interleave the snapshot with an in-flight GetRunAsync
        // call. GetRunAsync is mocked to return null synchronously, so the API call completes before the
        // snapshot fires. The race path (snapshot arriving while GetRunAsync is still awaited) is not
        // exercised. A tautological pass is possible: the model is null not because of the race but because
        // GetRunAsync returned null and no snapshot-before-summary code path was triggered.
        var runModel = GetField<PipelineRun?>(cut.Instance, "_activeModalRunModel");
        runModel.Should().NotBeNull("snapshot must construct the model even before API response");
        runModel!.CurrentStep.Should().Be(PipelineStep.VerifyingBaseline);
    }

    [Fact]
    public async Task HandleRunStateSnapshot_WhenActiveModalRunModelIsNull_DoesNotThrow()
    {
        // Dispatch a snapshot when no modal is open (no _selectedRunId set).
        // Should silently return without NullReferenceException.
        var snapshot = new RunStateSnapshot
        {
            CurrentStep = PipelineStep.GeneratingCode,
            HighWaterMark = PipelineStep.GeneratingCode,
        };

        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onRunStateSnapshot);

        // No OpenRunDetail called — _selectedRunId is null
        var act = async () => await cut.InvokeAsync(() => _onRunStateSnapshot!("some-run", snapshot));
        await act.Should().NotThrowAsync("snapshot with no modal open must be silently ignored");
    }

    [Fact]
    public async Task HandleStepTransition_WhenActiveModalRunModelSeeded_AdvancesHighWaterMark()
    {
        var runId = "hwm-advance-run";
        var snapshot = new RunStateSnapshot
        {
            CurrentStep = PipelineStep.AnalyzingCode,
            HighWaterMark = PipelineStep.AnalyzingCode,
            IssueIdentifier = "org/repo#1",
        };

        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onRunStateSnapshot);
        Assert.NotNull(_onStepTransition);

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [runId])!;
        });

        // Seed model via snapshot
        await cut.InvokeAsync(() => _onRunStateSnapshot!(runId, snapshot));

        // Advance step (GeneratingCode is logically after AnalyzingCode)
        await cut.InvokeAsync(() => _onStepTransition!(runId, PipelineStep.GeneratingCode, DateTimeOffset.UtcNow));

        var runModel = GetField<PipelineRun?>(cut.Instance, "_activeModalRunModel");
        runModel.Should().NotBeNull();
        runModel!.CurrentStep.Should().Be(PipelineStep.GeneratingCode);
        runModel.HighWaterMark.Should().Be(PipelineStep.GeneratingCode,
            "HighWaterMark must advance to GeneratingCode when step transitions forward");
    }

    [Fact]
    public async Task HandleStepTransition_WhenStepIsBeforeHighWaterMark_DoesNotLowerHighWaterMark()
    {
        var runId = "hwm-retry-run";
        // Seed with HighWaterMark at GeneratingCode (forward)
        var snapshot = new RunStateSnapshot
        {
            CurrentStep = PipelineStep.GeneratingCode,
            HighWaterMark = PipelineStep.GeneratingCode,
            IssueIdentifier = "org/repo#2",
        };

        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onRunStateSnapshot);
        Assert.NotNull(_onStepTransition);

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [runId])!;
        });

        await cut.InvokeAsync(() => _onRunStateSnapshot!(runId, snapshot));

        // Retry: step regresses to AnalyzingCode (logically before GeneratingCode)
        await cut.InvokeAsync(() => _onStepTransition!(runId, PipelineStep.AnalyzingCode, DateTimeOffset.UtcNow));

        var runModel = GetField<PipelineRun?>(cut.Instance, "_activeModalRunModel");
        runModel.Should().NotBeNull();
        runModel!.CurrentStep.Should().Be(PipelineStep.AnalyzingCode);
        runModel.HighWaterMark.Should().Be(PipelineStep.GeneratingCode,
            "HighWaterMark must not regress — remains at GeneratingCode after retry");
    }

    [Fact]
    public async Task HandleStepTransition_WhenActiveModalRunModelIsNull_DoesNotThrow()
    {
        // If the snapshot has not yet arrived (model not seeded), step transitions
        // must not throw NullReferenceException.
        var runId = "step-no-model-run";
        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onStepTransition);

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [runId])!;
        });

        // No snapshot — model is null; step transition must guard safely
        var act = async () => await cut.InvokeAsync(() =>
            _onStepTransition!(runId, PipelineStep.GeneratingCode, DateTimeOffset.UtcNow));
        await act.Should().NotThrowAsync("step transition before snapshot must not throw");

        // _activeModalCurrentStep should still be updated
        await cut.InvokeAsync(() => { });
        var step = GetField<PipelineStep?>(cut.Instance, "_activeModalCurrentStep");
        step.Should().Be(PipelineStep.GeneratingCode);
    }

    [Fact]
    public async Task HandleStepTransition_RunningEnvironmentSetup_UsesLogicalOrderNotRawEnumInt()
    {
        // RunningEnvironmentSetup = 29 (highest raw enum value) but logical position 2.
        // If HighWaterMark is at SyncingBrainRepoPreRun (logical 3), a transition to
        // RunningEnvironmentSetup (logical 2) must NOT advance HighWaterMark.
        var runId = "env-setup-run";
        var snapshot = new RunStateSnapshot
        {
            CurrentStep = PipelineStep.SyncingBrainRepoPreRun,
            HighWaterMark = PipelineStep.SyncingBrainRepoPreRun,
            IssueIdentifier = "org/repo#3",
        };

        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onRunStateSnapshot);
        Assert.NotNull(_onStepTransition);

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [runId])!;
        });

        await cut.InvokeAsync(() => _onRunStateSnapshot!(runId, snapshot));

        // Transition to RunningEnvironmentSetup (logical position 2 < 3)
        await cut.InvokeAsync(() =>
            _onStepTransition!(runId, PipelineStep.RunningEnvironmentSetup, DateTimeOffset.UtcNow));

        var runModel = GetField<PipelineRun?>(cut.Instance, "_activeModalRunModel");
        runModel.Should().NotBeNull();
        runModel!.HighWaterMark.Should().Be(PipelineStep.SyncingBrainRepoPreRun,
            "RunningEnvironmentSetup has logical order 2 < SyncingBrainRepoPreRun logical 3; HighWaterMark must not regress");
    }

    [Fact]
    public async Task DismissRunDetailModal_ClearsActiveModalRunModel()
    {
        var runId = "dismiss-model-run";
        var snapshot = new RunStateSnapshot
        {
            CurrentStep = PipelineStep.AnalyzingCode,
            HighWaterMark = PipelineStep.AnalyzingCode,
            IssueIdentifier = "org/repo#5",
        };

        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onRunStateSnapshot);

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [runId])!;
        });

        await cut.InvokeAsync(() => _onRunStateSnapshot!(runId, snapshot));

        GetField<PipelineRun?>(cut.Instance, "_activeModalRunModel").Should().NotBeNull();

        await cut.InvokeAsync(async () =>
        {
            var dismissMethod = typeof(AgentMonitoring).GetMethod("DismissRunDetailModal",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)dismissMethod!.Invoke(cut.Instance, null)!;
        });

        GetField<PipelineRun?>(cut.Instance, "_activeModalRunModel")
            .Should().BeNull("DismissRunDetailModal must clear _activeModalRunModel");
    }

    [Fact]
    public async Task CompletedRun_ModalOpen_WhenBrainRepoUsedTrue_BrainProviderConfigIdIsSet()
    {
        var runGuid = Guid.NewGuid();
        var completedSummary = new PipelineRunSummary
        {
            RunId = runGuid.ToString(),
            IssueIdentifier = "org/repo#10",
            IssueTitle = "Brain Run",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            BrainRepoUsed = true,
        };

        _mockRunHistoryClient.Setup(c => c.GetRunAsync(runGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(completedSummary);

        var cut = Render<AgentMonitoring>();

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [runGuid.ToString()])!;
        });

        var runModel = GetField<PipelineRun?>(cut.Instance, "_activeModalRunModel");
        runModel.Should().NotBeNull("completed run with BrainRepoUsed=true should have a sidebar model");
        // TODO [WARNING]: Assertion is too weak — NotBeNull() passes for any non-null value including a single
        // space or an empty string. The code sets the sentinel to "placeholder"; if that changes to a value the
        // sidebar rejects (e.g. empty string), this test will still pass. Use NotBeNullOrEmpty() to at minimum
        // verify the sentinel is a non-empty string that PipelineSidebar.IsStepHidden will treat as "used".
        runModel!.BrainProviderConfigId.Should().NotBeNull(
            "BrainProviderConfigId must be set to a placeholder so brain steps are not hidden for completed runs that used a brain repo");
    }

    [Fact]
    public async Task CompletedRun_ModalOpen_WhenBrainRepoUsedFalse_BrainProviderConfigIdIsNull()
    {
        var runGuid = Guid.NewGuid();
        var completedSummary = new PipelineRunSummary
        {
            RunId = runGuid.ToString(),
            IssueIdentifier = "org/repo#11",
            IssueTitle = "No Brain Run",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            BrainRepoUsed = false,
        };

        _mockRunHistoryClient.Setup(c => c.GetRunAsync(runGuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(completedSummary);

        var cut = Render<AgentMonitoring>();

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [runGuid.ToString()])!;
        });

        var runModel = GetField<PipelineRun?>(cut.Instance, "_activeModalRunModel");
        runModel.Should().NotBeNull();
        runModel!.BrainProviderConfigId.Should().BeNull(
            "BrainProviderConfigId must remain null when BrainRepoUsed=false so brain steps are hidden");
    }

    [Fact]
    public async Task HandleReconnected_WhenRunModalOpen_ReSubscribesToRun()
    {
        _mockHub.SetupGet(h => h.State).Returns(HubConnectionState.Connected);
        _mockHub.Setup(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = Render<AgentMonitoring>();

        // Open modal — sets _selectedRunId
        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, ["reconnect-run"])!;
        });

        // The initial SubscribeToRun call
        _mockHub.Verify(
            h => h.InvokeAsync(HubMethodNames.SubscribeToRun, It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Simulate reconnect
        Assert.NotNull(_reconnectedHandler);
        await _reconnectedHandler!("new-connection-id");
        await cut.InvokeAsync(() => { }); // flush

        // SubscribeToRun should be called again after reconnect
        // TODO [WARNING]: Times.AtLeast(2) would pass even if the reconnect handler triggered 10 extra
        // subscriptions (e.g. a loop or multiple handler registrations). Use Times.Exactly(2) to catch
        // accidental duplicate-registration bugs where HubConnection.Reconnected += HandleReconnected
        // is called multiple times.
        _mockHub.Verify(
            h => h.InvokeAsync(HubMethodNames.SubscribeToRun, It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2),
            "SubscribeToRun must be re-invoked after hub reconnect when a modal is open");
    }

    [Fact]
    public async Task HandleReconnected_WhenNoModalOpen_DoesNotSubscribe()
    {
        _mockHub.SetupGet(h => h.State).Returns(HubConnectionState.Connected);
        _mockHub.Setup(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = Render<AgentMonitoring>();

        // No modal open — _selectedRunId is null
        Assert.NotNull(_reconnectedHandler);
        await _reconnectedHandler!("new-connection-id");
        await cut.InvokeAsync(() => { });

        _mockHub.Verify(
            h => h.InvokeAsync(HubMethodNames.SubscribeToRun, It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "SubscribeToRun must not be called on reconnect when no run modal is open");
    }

    // ── ApplySnapshotToRunModel bug fixes (Issue #2253) ───────────────────────

    [Fact]
    public async Task ApplySnapshotToRunModel_WhenSnapshotHasCodeReviewCounts_SetsCountsOnModel()
    {
        // Regression test for Bug 1: code review counts always 0/0/0 after snapshot restore.
        // Must be RED against unfixed code (SetCodeReviewCounts never called) and GREEN after fix.
        var runId = "code-review-counts-run";
        var snapshot = new RunStateSnapshot
        {
            CurrentStep = PipelineStep.ReviewingCode,
            HighWaterMark = PipelineStep.ReviewingCode,
            IssueIdentifier = "org/repo#100",
            IssueTitle = "Code Review Counts Test",
            CodeReviewCriticalCount = 3,
            CodeReviewWarningCount = 7,
            CodeReviewSuggestionCount = 2,
        };

        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onRunStateSnapshot);

        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [runId])!;
        });

        await cut.InvokeAsync(() => _onRunStateSnapshot!(runId, snapshot));

        var runModel = GetField<PipelineRun?>(cut.Instance, "_activeModalRunModel");
        runModel.Should().NotBeNull("snapshot must seed the sidebar view model");
        // TODO [WARNING]: This test only covers the model-construction path (where _activeModalRunModel is null
        // when the snapshot arrives). The existing-model path — where the model already exists and
        // ApplySnapshotToRunModel is called on it directly — is not independently covered for non-zero
        // CodeReviewCriticalCount. In practice there is no fix gap because SetCodeReviewCounts is called
        // unconditionally in ApplySnapshotToRunModel, but the test would remain green even if
        // SetCodeReviewCounts were accidentally placed inside the if (_activeModalRunModel == null) block.
        // Consider adding a companion test that fires a second snapshot onto an already-initialized model.
        runModel!.CodeReviewCriticalCount.Should().Be(3,
            "ApplySnapshotToRunModel must call SetCodeReviewCounts with critical=3 from the snapshot");
        runModel.CodeReviewWarningCount.Should().Be(7,
            "ApplySnapshotToRunModel must call SetCodeReviewCounts with warning=7 from the snapshot");
        runModel.CodeReviewSuggestionCount.Should().Be(2,
            "ApplySnapshotToRunModel must call SetCodeReviewCounts with suggestion=2 from the snapshot");
    }

    [Fact]
    public async Task ApplySnapshotToRunModel_OnReconnect_ClearsQualityGateHistoryBeforeReapplying()
    {
        // Regression test for Bug 2: QualityGateHistory duplicates on hub reconnect.
        // Must be RED against unfixed code (no drain before re-enqueue) and GREEN after fix.
        _mockHub.SetupGet(h => h.State).Returns(HubConnectionState.Connected);
        _mockHub.Setup(h => h.InvokeAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var runId = "qg-history-reconnect-run";
        var snapshot = new RunStateSnapshot
        {
            CurrentStep = PipelineStep.RunningQualityGates,
            HighWaterMark = PipelineStep.RunningQualityGates,
            IssueIdentifier = "org/repo#200",
            IssueTitle = "QG History Reconnect Test",
            QualityGateHistory =
            [
                new QualityGateReport
                {
                    Compilation = new GateResult { GateName = "Compilation", Passed = true },
                    Tests = new GateResult { GateName = "Tests", Passed = true },
                },
                new QualityGateReport
                {
                    Compilation = new GateResult { GateName = "Compilation", Passed = false },
                    Tests = new GateResult { GateName = "Tests", Passed = false },
                },
            ],
        };

        var cut = Render<AgentMonitoring>();
        Assert.NotNull(_onRunStateSnapshot);
        Assert.NotNull(_reconnectedHandler);

        // Open modal and fire initial snapshot
        await cut.InvokeAsync(async () =>
        {
            var method = typeof(AgentMonitoring).GetMethod("OpenRunDetail",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            await (Task)method!.Invoke(cut.Instance, [runId])!;
        });

        await cut.InvokeAsync(() => _onRunStateSnapshot!(runId, snapshot));

        // Sanity check: initial snapshot produces exactly 2 entries
        var runModelBefore = GetField<PipelineRun?>(cut.Instance, "_activeModalRunModel");
        runModelBefore.Should().NotBeNull();
        runModelBefore!.QualityGateHistory.Count.Should().Be(2,
            "initial snapshot application must produce exactly 2 QualityGateHistory entries");

        // Simulate reconnect: the reconnect handler calls SubscribeToRun again.
        // The server would re-push OnRunStateSnapshot; we simulate that by firing the snapshot again manually
        // after the reconnect handler runs. This is equivalent to the live path and avoids complex mock chaining.
        // TODO [WARNING]: This test fires _onRunStateSnapshot manually to simulate the server re-pushing after
        // reconnect, rather than verifying that HandleReconnected itself triggers the re-push. If
        // HandleReconnected is later changed to stop calling SubscribeToRun, the drain bug could re-emerge
        // without this test catching it. The actual regression guard is only the final
        // QualityGateHistory.Count.Should().Be(2) assertion at the end of the test.
        await _reconnectedHandler!("new-connection-id");
        await cut.InvokeAsync(() => { }); // flush reconnect handler's InvokeAsync

        // Verify SubscribeToRun was called exactly twice (initial open + reconnect) — not more
        _mockHub.Verify(
            h => h.InvokeAsync(HubMethodNames.SubscribeToRun, It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "SubscribeToRun must be called exactly twice: once on open and once on reconnect");

        // Fire the snapshot a second time (simulating the server re-pushing on re-subscribe)
        await cut.InvokeAsync(() => _onRunStateSnapshot!(runId, snapshot));

        // After drain + re-enqueue, count must still be 2 — not 4
        var runModelAfter = GetField<PipelineRun?>(cut.Instance, "_activeModalRunModel");
        runModelAfter.Should().NotBeNull();
        runModelAfter!.QualityGateHistory.Count.Should().Be(2,
            "QualityGateHistory must be drained before re-applying snapshot; Count must remain 2, not 4");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static T GetField<T>(object instance, string fieldName)
    {
        var field = instance.GetType()
            .GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (T)field!.GetValue(instance)!;
    }

    private static void SetField(object instance, string fieldName, object? value)
    {
        var field = instance.GetType()
            .GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(instance, value);
    }
}
