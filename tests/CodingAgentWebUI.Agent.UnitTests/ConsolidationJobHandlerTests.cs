using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="ConsolidationJobHandler"/>.
/// Verifies consolidation job assignment, rejection, execution, failure reporting,
/// and slot management without requiring full <see cref="AgentWorkerServiceDependencies"/> construction.
/// </summary>
public class ConsolidationJobHandlerTests
{
    // ── Setup helpers ─────────────────────────────────────────────────────

    private static (ConsolidationJobHandler Handler, AgentJobSlotManager SlotManager, AgentConnectionLifecycle Lifecycle)
        CreateHandler(IConsolidationExecutor? consolidationExecutor = null, Serilog.ILogger? logger = null)
    {
        var mockLogger = logger ?? new Mock<Serilog.ILogger>().Object;
        var hm = TestAgentWorkerServiceFactory.CreateTestHubManager(mockLogger);
        var hmFactory = TestAgentWorkerServiceFactory.CreateTestHubManagerFactory(mockLogger);
        var buffer = new CriticalMessageBuffer();
        var pipeline = CodingAgentWebUI.Infrastructure.Resilience.ResiliencePipelineFactory.CreateSignalRPipeline(mockLogger);
        var signalRReporter = new SignalRCompletionReporter(hm, pipeline, buffer, mockLogger);
        var slotManager = new AgentJobSlotManager(() => Task.CompletedTask);
        var lifetime = Mock.Of<IHostApplicationLifetime>();
        var lifecycle = new AgentConnectionLifecycle(hm, hmFactory, signalRReporter, slotManager,
            new AgentId("test-consol"), lifetime, mockLogger);

        var executor = consolidationExecutor ?? new Mock<IConsolidationExecutor>().Object;
        var handler = new ConsolidationJobHandler(lifecycle, slotManager, executor, mockLogger);
        return (handler, slotManager, lifecycle);
    }

    private static ConsolidationJobMessage CreateMessage(string jobId = "consol-1") => new()
    {
        JobId = jobId,
        Type = ConsolidationRunType.BrainConsolidation,
        ProviderConfigs = [],
        PipelineConfiguration = new PipelineConfiguration()
    };

    private static T? GetPrivateField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {obj.GetType().Name}");
        return (T?)field.GetValue(obj);
    }

    private static void SetPrivateField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {obj.GetType().Name}");
        field.SetValue(obj, value);
    }

    // ── HandleAssignConsolidationJobAsync ─────────────────────────────────

    [Fact]
    public async Task HandleAssignConsolidationJobAsync_WhenBusy_RejectsWithHubNotification()
    {
        var (handler, slotManager, _) = CreateHandler();

        // Simulate busy agent
        SetPrivateField(slotManager, "_activeJobId", (JobId?)(JobId)"existing-job");
        SetPrivateField(slotManager, "_isBusy", true);

        var message = CreateMessage("new-consol-job");
        await handler.HandleAssignConsolidationJobAsync(message);

        // Slot must remain with the existing job — not taken by the new message
        GetPrivateField<JobId?>(slotManager, "_activeJobId")
            .Should().Be((JobId)"existing-job", "busy rejection must not overwrite existing job slot");
    }

    [Fact]
    public async Task HandleAssignConsolidationJobAsync_WhenIdle_AcquiresSlotAndStartsTask()
    {
        var (handler, slotManager, _) = CreateHandler();

        var message = CreateMessage("idle-consol-job");
        await handler.HandleAssignConsolidationJobAsync(message);

        // TODO: Assertion is too weak — only checks that _activeJobTask is non-null, not that the slot was
        // acquired for "idle-consol-job". The background task (with a default mock executor) runs concurrently
        // and may have already released the slot by the time the assertion runs. A stronger check would assert
        // _activeJobId == "idle-consol-job" immediately after the call, or use a blocking executor mock that
        // holds the slot open for the duration of the assertion.
        var activeTask = GetPrivateField<Task?>(slotManager, "_activeJobTask");
        activeTask.Should().NotBeNull("HandleAssignConsolidationJobAsync must set the active job task");
    }

    // ── RunConsolidationTaskAsync ─────────────────────────────────────────

    [Fact]
    public async Task RunConsolidationTaskAsync_ExecutorThrows_ReportsFailureAndReleasesSlot()
    {
        var throwingExecutor = new Mock<IConsolidationExecutor>();
        throwingExecutor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<ConsolidationJobMessage>(),
                It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("consolidation boom"));

        var (handler, slotManager, _) = CreateHandler(throwingExecutor.Object);

        // Acquire slot first (simulating what HandleAssignConsolidationJobAsync does)
        slotManager.TryAcquireJobSlot("throw-consol-job", out _);

        var message = CreateMessage("throw-consol-job");
        using var cts = new CancellationTokenSource();
        await handler.RunConsolidationTaskAsync(message, cts.Token);

        // Slot must be released in finally block even when executor throws
        GetPrivateField<JobId?>(slotManager, "_activeJobId")
            .Should().BeNull("slot must be released in finally block regardless of executor exception");
    }

    [Fact]
    public async Task RunConsolidationTaskAsync_SuccessfulExecution_ReleasesSlot()
    {
        var successExecutor = new Mock<IConsolidationExecutor>();
        successExecutor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<ConsolidationJobMessage>(),
                It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationJobResult { JobId = "success-consol-job", Success = true });

        var (handler, slotManager, _) = CreateHandler(successExecutor.Object);

        slotManager.TryAcquireJobSlot("success-consol-job", out _);

        var message = CreateMessage("success-consol-job");
        using var cts = new CancellationTokenSource();
        await handler.RunConsolidationTaskAsync(message, cts.Token);

        // Slot released via ReleaseJobSlotAndSignalReadyAsync
        GetPrivateField<JobId?>(slotManager, "_activeJobId")
            .Should().BeNull("slot must be released after successful execution");
    }

    // ── RejectConsolidationJobBusyAsync ──────────────────────────────────

    [Fact]
    public async Task RejectConsolidationJobBusyAsync_HubThrows_CompletesWithoutThrowing()
    {
        var (handler, _, _) = CreateHandler();

        // Hub is disconnected — InvokeAsync will throw; must be swallowed
        var act = async () => await handler.RejectConsolidationJobBusyAsync("busy-job", "some-other-job", null);
        await act.Should().NotThrowAsync(
            "RejectConsolidationJobBusyAsync must swallow hub exceptions to not crash the event handler");
    }

    // ── ReportConsolidationFailureAsync ──────────────────────────────────

    [Fact]
    public async Task ReportConsolidationFailureAsync_HubThrows_DoesNotPropagate()
    {
        var (handler, _, _) = CreateHandler();

        // Hub is disconnected — InvokeAsync will throw; must be swallowed
        var act = async () => await handler.ReportConsolidationFailureAsync("fail-job", "some error");
        await act.Should().NotThrowAsync(
            "ReportConsolidationFailureAsync must swallow hub exceptions");
    }

    // ── HandleAssignConsolidationJobAsync: null JobCancellationToken ──────

    /// <summary>
    /// Structural regression guard for issue #2103.
    /// Verifies that the null-forgiving operator on JobCancellationToken has been replaced by a
    /// defensive null check matching the ChatJobHandler pattern.
    ///
    /// A behavioral test through HandleAssignConsolidationJobAsync cannot force the null-token
    /// path: TryAcquireJobSlot creates a fresh CTS synchronously, and JobCancellationToken is
    /// read in the very next synchronous statement — there is no window to null the CTS from
    /// outside. The guard is therefore verified structurally: the fix is confirmed correct at
    /// code review, and this test ensures it is never silently reverted.
    /// </summary>
    [Fact]
    public void HandleAssignConsolidationJobAsync_JobCancellationToken_UsesDefensiveNullCheck_NotNullForgivingOperator()
    {
        var sourceDir = GetSourceDirectory();
        var source = File.ReadAllText(
            Path.Combine(sourceDir, "src", "CodingAgentWebUI.Agent", "ConsolidationJobHandler.cs"));

        // Extract the HandleAssignConsolidationJobAsync method body
        var methodStart = source.IndexOf("public async Task HandleAssignConsolidationJobAsync(", StringComparison.Ordinal);
        methodStart.Should().BeGreaterThan(0, "HandleAssignConsolidationJobAsync must exist in ConsolidationJobHandler.cs");

        // Find the end of the method (the next public/internal/private/protected method or end of class)
        // TODO: [WARNING] Method-boundary detection only covers "public" and "internal" visibility modifiers.
        // If a "private" or "protected" method immediately follows HandleAssignConsolidationJobAsync, nextMethodStart
        // will be -1 and methodBody will capture the remainder of the file, causing the NotContain assertion to scan
        // unrelated code. Fix: also check for "\n    private " and "\n    protected " (and "private protected") and
        // take the minimum positive index. A regex anchored to the method's braces would be even more robust.
        var nextMethodStart = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        if (nextMethodStart < 0) nextMethodStart = source.IndexOf("\n    internal ", methodStart + 1, StringComparison.Ordinal);
        var methodBody = nextMethodStart > 0
            ? source.Substring(methodStart, nextMethodStart - methodStart)
            : source.Substring(methodStart);

        // The null-forgiving operator must be gone
        methodBody.Should().NotContain(
            "JobCancellationToken!",
            "the null-forgiving operator on JobCancellationToken must be replaced by a defensive null check (issue #2103)");

        // The defensive pattern from ChatJobHandler must be present
        methodBody.Should().Contain(
            "JobCancellationToken is not { }",
            "HandleAssignConsolidationJobAsync must use the defensive null-check pattern matching ChatJobHandler");

        // ReleaseJobSlotAndSignalReadyAsync must be called on the null path
        // TODO: [WARNING] This assertion is satisfied by the string appearing anywhere in the extracted text,
        // including in comments or unrelated branches. It does not verify that ReleaseJobSlotAndSignalReadyAsync
        // is actually called on the null-token path at runtime. If the call were removed from the null branch but
        // the string kept in a comment, this assertion would still pass. Consider adding a behavioral test that
        // mocks JobCancellationToken as null and verifies ReleaseJobSlotAndSignalReadyAsync is invoked — if the
        // null path ever becomes reachable through the public API.
        methodBody.Should().Contain(
            "ReleaseJobSlotAndSignalReadyAsync",
            "the slot must be released via ReleaseJobSlotAndSignalReadyAsync when JobCancellationToken is null");
    }

    private static string GetSourceDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find solution root");
    }
}
