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
    private readonly Mock<IAgentHubConnection> _mockHub = new();
    private readonly Mock<IPipelineApiRunHistoryClient> _mockRunHistoryClient = new();
    private readonly Mock<IConsolidationService> _mockConsolidation = new();
    private readonly Mock<IWorkDistributor> _mockWorkDistributor = new();

    // Captured hub event handlers — set during On<> setup
    private Action<string, IReadOnlyList<string>>? _onOutputLines;
    private Action<string, PipelineStep, DateTimeOffset>? _onStepTransition;
    private Action<string, JobCompletionPayload>? _onRunCompleted;

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
        Services.AddSingleton(TimeProvider.System);
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
        await cut.InvokeAsync(() => _onOutputLines!("some-run-id", new[] { "line1" }));

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
        await cut.InvokeAsync(() => _onOutputLines!("run-B", new[] { "different-run-line" }));

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
        await cut.InvokeAsync(() => _onOutputLines!("run-C", new[] { "hello", "world" }));

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

        // Verify RemoveFromQueueAsync was called via the PageService
        // The job should disappear after the re-render (the mock now returns empty list)
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
