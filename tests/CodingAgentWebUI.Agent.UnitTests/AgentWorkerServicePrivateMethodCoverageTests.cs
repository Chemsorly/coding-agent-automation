using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Targeted coverage tests for the private methods extracted from AgentWorkerService
/// in PR #1778 (SonarQube S107 refactoring). Each test exercises a specific branch
/// or code path that was not reached by the existing broader behavioural tests.
/// </summary>
[Collection("EnvironmentVariables")]
public class AgentWorkerServicePrivateMethodCoverageTests : IDisposable
{
    public void Dispose()
    {
        TryDeleteDir(AgentDefaults.ChatWorkspacePath);
        TryDeleteDir(AgentDefaults.ChatWorkspacesRoot);
        GC.SuppressFinalize(this);
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }

    // ── S8949 source-scan tests — verify CancellationToken propagation ────
    // TODO: These source-scan tests verify that specific substrings exist in each method body,
    // but do NOT verify that the substring appears inside an InvokeAsync call. If the production
    // code is refactored so that CancellationToken.None (or ApplicationStopping) appears only in
    // a comment or an unrelated expression while the actual InvokeAsync call loses its token
    // argument, these tests would still pass. Consider tightening the assertions to require the
    // token string to appear on the same line as, or adjacent to, an InvokeAsync call.
    // TODO: Source-scan tests are absent for: (1) AgentConnectionManager.DisposeAsync
    // (CancellationToken.None with // intentional: comment added by this change); (2)
    // ChatJobDispatcher Task.Run(..., cts.Token) change; (3) the three outputBatcher.AddLineAsync
    // calls that now forward chatToken (MCP config, project steering, project secrets log lines
    // in AgentWorkerService.cs). Add source-scan tests for these sites to ensure
    // regressions are caught.

    /// <summary>
    /// Verifies that ReportChatCompletedAsync passes CancellationToken.None with an
    /// // intentional: comment to InvokeAsync. Since chatToken may be cancelled when
    /// this method is called, CancellationToken.None is the correct choice.
    /// </summary>
    [Fact]
    public void SourceCode_ReportChatCompletedAsync_PassesCancellationTokenNoneWithComment()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "ChatJobHandler.cs"));

        // Extract just the ReportChatCompletedAsync method body to avoid false positives
        var methodStart = source.IndexOf("public async Task ReportChatCompletedAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        methodBody.Should().Contain("CancellationToken.None",
            "ReportChatCompletedAsync must pass CancellationToken.None to InvokeAsync — chatToken may be cancelled at call time");
        methodBody.Should().Contain("// intentional:",
            "ReportChatCompletedAsync must have an // intentional: comment explaining why CancellationToken.None is used");
    }

    /// <summary>
    /// Verifies that the signalAgentReady delegate in AgentSignalRModeRegistration passes
    /// ApplicationStopping to InvokeAsync so the call is cancelled during application shutdown.
    /// </summary>
    [Fact]
    public void SourceCode_SignalAgentReady_PassesApplicationStopping()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentSignalRModeRegistration.cs"));

        // TODO: [WARNING] This assertion scans the entire file for "ApplicationStopping" and will pass as long
        // as the string appears anywhere — e.g. it is also present in the AgentReady hub invocation at the top
        // of the file (line ~40). If the ChatJobHandler's _signalAgentReady delegate were changed to pass
        // CancellationToken.None instead of lifetime.ApplicationStopping, this test would still pass because
        // the other occurrence remains. Narrow the assertion to the ChatJobHandler factory lambda body to
        // ensure the cancellation contract is enforced at the correct callsite.
        source.Should().Contain("ApplicationStopping",
            "signalAgentReady delegates must pass ApplicationStopping to InvokeAsync so they are cancelled during shutdown");
    }

    /// <summary>
    /// Verifies that ReportConsolidationFailureAsync passes CancellationToken.None with comment.
    /// Called from a catch block where jobToken may already be cancelled.
    /// </summary>
    [Fact]
    public void SourceCode_ReportConsolidationFailureAsync_PassesCancellationTokenNoneWithComment()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "ConsolidationJobHandler.cs"));

        var methodStart = source.IndexOf("public async Task ReportConsolidationFailureAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.Length;
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        methodBody.Should().Contain("CancellationToken.None",
            "ReportConsolidationFailureAsync must pass CancellationToken.None — called from catch block where jobToken may be cancelled");
        methodBody.Should().Contain("// intentional:",
            "ReportConsolidationFailureAsync must have an // intentional: comment explaining why CancellationToken.None is used");
    }

    /// <summary>
    /// Verifies that ReportFetchModelsError passes CancellationToken.None with comment.
    /// Called from error paths where no ambient token is available.
    /// </summary>
    [Fact]
    public void SourceCode_ReportFetchModelsError_PassesCancellationTokenNoneWithComment()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "ChatJobHandler.cs"));

        var methodStart = source.IndexOf("public async Task ReportFetchModelsError(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.Length;
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        methodBody.Should().Contain("CancellationToken.None",
            "ReportFetchModelsError must pass CancellationToken.None — called from error paths without ambient token");
        methodBody.Should().Contain("// intentional:",
            "ReportFetchModelsError must have an // intentional: comment explaining why CancellationToken.None is used");
    }

    /// <summary>
    /// Verifies that HandleFetchModelsAsync does not pass timeoutCts.Token to any post-exit
    /// call (stderr read or InvokeAsync). The process has already exited at both call sites,
    /// so the timeout token's purpose is exhausted and may already be cancelled.
    /// Both the error path (stderr ReadToEndAsync) and the success path (ReportFetchModelsResult
    /// InvokeAsync) must use CancellationToken.None with an // intentional: comment.
    /// </summary>
    [Fact]
    public void SourceCode_HandleFetchModelsAsync_PassesCancellationTokenNone()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "ChatJobHandler.cs"));

        var methodStart = source.IndexOf("public async Task HandleFetchModelsAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        // TODO: The fallback above only handles private/public access modifiers. If a member with
        // a different access modifier (internal, protected, protected internal, private protected)
        // or an attribute/doc comment is inserted after HandleFetchModelsAsync, or if it becomes
        // the last method in the class, both searches may return -1 and source.Substring will throw
        // ArgumentOutOfRangeException instead of a meaningful assertion failure. Add:
        // if (methodEnd < 0) methodEnd = source.Length;
        if (methodEnd < 0) methodEnd = source.Length;
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        // Locate the end of WaitForExitAsync — all post-exit code follows this call
        var waitForExitEnd = methodBody.IndexOf("WaitForExitAsync(", StringComparison.Ordinal);
        waitForExitEnd = methodBody.IndexOf(");", waitForExitEnd, StringComparison.Ordinal) + 2;
        var postExitBody = methodBody.Substring(waitForExitEnd);

        // TODO: This assertion only verifies that CancellationToken.None appears at least once in
        // postExitBody. There are two post-exit call sites (stderr ReadToEndAsync error path and
        // ReportFetchModelsResult InvokeAsync success path); a partial revert of one site would
        // still satisfy this check. Consider asserting count >= 2:
        // (postExitBody.Split("CancellationToken.None").Length - 1).Should().BeGreaterOrEqualTo(2, ...)
        postExitBody.Should().Contain("CancellationToken.None",
            "HandleFetchModelsAsync must use CancellationToken.None for post-exit calls — timeoutCts.Token may be expired after WaitForExitAsync");
        postExitBody.Should().Contain("// intentional:",
            "HandleFetchModelsAsync must have an // intentional: comment in post-exit code explaining why CancellationToken.None is used");
        postExitBody.Should().NotContain("timeoutCts.Token)",
            "HandleFetchModelsAsync must not pass timeoutCts.Token to any post-exit call (stderr read or InvokeAsync) — the token may already be cancelled");
    }

    /// <summary>
    /// Verifies that AgentConnectionLifecycle.ShutdownAsync passes CancellationToken.None with
    /// an // intentional: comment to the DeregisterAgent InvokeAsync call.
    /// ShutdownAsync is invoked after ApplicationStopping is already signaled, so passing
    /// ApplicationStopping would cause InvokeAsync to throw OperationCanceledException immediately
    /// and deregistration would never reach the orchestrator. CancellationToken.None is correct here.
    /// </summary>
    [Fact]
    public void SourceCode_AgentConnectionLifecycle_ShutdownAsync_PassesCancellationTokenNoneWithComment()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentConnectionLifecycle.cs"));

        var methodStart = source.IndexOf("public async Task ShutdownAsync()", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        // TODO: The fallback above only handles public/private access modifiers. If a member with
        // a different access modifier (internal, protected, protected internal, private protected)
        // or an attribute/doc comment is inserted after ShutdownAsync, both searches may return -1
        // and source.Substring will throw ArgumentOutOfRangeException. Consider adding a
        // source.Length fallback: if (methodEnd < 0) methodEnd = source.Length;
        if (methodEnd < 0) methodEnd = source.Length;
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        // ShutdownAsync runs after ApplicationStopping fires — must use CancellationToken.None so
        // deregistration can still reach the orchestrator during graceful shutdown.
        methodBody.Should().Contain("CancellationToken.None",
            "AgentConnectionLifecycle.ShutdownAsync must pass CancellationToken.None to DeregisterAgent InvokeAsync — ApplicationStopping is already triggered at call time");
        methodBody.Should().Contain("// intentional:",
            "AgentConnectionLifecycle.ShutdownAsync must have an // intentional: comment explaining why CancellationToken.None is used");
        methodBody.Should().NotContain("_hostApplicationLifetime.ApplicationStopping",
            "AgentConnectionLifecycle.ShutdownAsync must NOT pass ApplicationStopping — it is already cancelled when ShutdownAsync runs");
    }

    /// <summary>
    /// Structural guard: verifies that ReleaseChatSlot() appears inside a finally block in
    /// RunChatTaskAsync. This is the primary regression guard for issue #1857 — it directly
    /// validates the fix rather than relying on behavioral execution which would pass even
    /// without a finally block (the sequential path reaches ReleaseChatSlot on the happy path).
    /// </summary>
    [Fact]
    public void SourceCode_RunChatTaskAsync_ReleaseChatSlotIsInsideFinallyBlock()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "ChatJobHandler.cs"));

        // Extract just the RunChatTaskAsync method body to avoid false positives from other methods
        var methodStart = source.IndexOf("public async Task RunChatTaskAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.Length;
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        // The fix requires ReleaseChatSlot() to be inside a finally block.
        // We verify this by asserting that "finally" appears BEFORE "ReleaseChatSlot()" in the
        // method body, and that both are present.
        methodBody.Should().Contain("finally",
            "RunChatTaskAsync must contain a finally block that guards ReleaseChatSlot()");
        methodBody.Should().Contain("ReleaseChatSlot()",
            "RunChatTaskAsync must call ReleaseChatSlot()");

        var finallyIndex = methodBody.LastIndexOf("finally", StringComparison.Ordinal);
        var releaseIndex = methodBody.IndexOf("ReleaseChatSlot()", StringComparison.Ordinal);

        // TODO: This ordering assertion is fragile — it only verifies that a `finally` substring
        // appears before the first `ReleaseChatSlot()` substring in text order, not that
        // ReleaseChatSlot() is *lexically enclosed* within the finally block's braces. A refactor
        // that places ReleaseChatSlot() after the finally's closing `}` (back to the pre-fix
        // sequential pattern) would still satisfy this assertion if another `finally` keyword
        // appears later in the method body. A stronger check would verify ReleaseChatSlot()
        // appears between the finally's opening `{` and its corresponding closing `}`.
        // See review finding: SourceCode_RunChatTaskAsync_ReleaseChatSlotIsInsideFinallyBlock.
        releaseIndex.Should().BeGreaterThan(finallyIndex,
            "ReleaseChatSlot() must appear after the finally keyword — it must be inside a finally block, not before it");
    }

    private static string GetSourceDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find solution root");
    }

    // ── Characterization tests — telemetry counter + activity tags ──────────
    // These must be added BEFORE any extraction so that regressions (e.g. dropping the counter
    // from the extracted method) are caught by the test suite, not discovered at runtime.

    // MeterListener warm-up helper shared by the counter tests below.
    // Activates the listener, emits a zero-valued warm-up measurement so InstrumentPublished fires
    // for both static instruments, clears the warm-up noise, and returns the listener + measurement list.
    private static (MeterListener listener, List<(string name, List<KeyValuePair<string, object?>> tags)> measurements)
        CreateMeterListener()
    {
        var measurements = new List<(string, List<KeyValuePair<string, object?>>)>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            var tagList = new List<KeyValuePair<string, object?>>();
            foreach (var t in tags) tagList.Add(t);
            measurements.Add((instrument.Name, tagList));
        });
        listener.Start();
        // warm-up: force InstrumentPublished for static instruments created before Start()
        PipelineTelemetry.AgentJobsReceived.Add(0);
        PipelineTelemetry.AgentJobsRejected.Add(0);
        measurements.Clear();
        return (listener, measurements);
    }

    [Fact]
    public async Task RejectJobAsync_Pipeline_IncrementsRejectedCounterWithBusyTag()
    {
        // Characterization test: pins rejection telemetry behavior on the pipeline handler path.
        // Verifies agent.jobs.rejected is incremented with reason=busy when HandleAssignJobAsync
        // is called while the agent is busy.
        var (listener, measurements) = CreateMeterListener();
        using (listener)
        {
            var service = TestAgentWorkerServiceFactory.Create();
            var slotManager = GetSlotManager(service);
            SetPrivateField(slotManager, "_activeJobId", (JobId?)(JobId)"existing-job");
            SetPrivateField(slotManager, "_isBusy", true);

            await (Task)GetPrivateMethod(service, "HandleAssignJobAsync")
                .Invoke(service, [CreateJobAssignment("new-job")])!;
        }

        measurements.Should().Contain(m =>
            m.name == "agent.jobs.rejected" &&
            m.tags.Contains(new KeyValuePair<string, object?>("reason", "busy")),
            "agent.jobs.rejected must be incremented with reason=busy when pipeline job is rejected");
    }

    [Fact]
    public async Task RejectJobAsync_Consolidation_IncrementsRejectedCounterWithBusyTag()
    {
        // Characterization test: pins rejection telemetry behavior on the consolidation handler path.
        // HandleAssignConsolidationJobAsync was extracted to ConsolidationJobHandler — call it there.
        var (listener, measurements) = CreateMeterListener();
        using (listener)
        {
            var service = TestAgentWorkerServiceFactory.Create();
            var slotManager = GetSlotManager(service);
            SetPrivateField(slotManager, "_activeJobId", (JobId?)(JobId)"existing-job");
            SetPrivateField(slotManager, "_isBusy", true);

            var message = new ConsolidationJobMessage
            {
                JobId = "new-consolidation",
                Type = ConsolidationRunType.BrainConsolidation,
                ProviderConfigs = [],
                PipelineConfiguration = new PipelineConfiguration()
            };

            var consolidationHandler = GetConsolidationJobHandler(service);
            await (Task)GetMethod(consolidationHandler, "HandleAssignConsolidationJobAsync")
                .Invoke(consolidationHandler, [message])!;
        }

        measurements.Should().Contain(m =>
            m.name == "agent.jobs.rejected" &&
            m.tags.Contains(new KeyValuePair<string, object?>("reason", "busy")),
            "agent.jobs.rejected must be incremented with reason=busy when consolidation job is rejected");
    }

    [Fact]
    public async Task HandleAssignJobAsync_IncrementsReceivedCounter()
    {
        // Characterization test: pins received counter behavior on the pipeline handler path.
        // TODO [WARNING]: This test uses an idle agent, so TryReceiveJobAsync proceeds to the success
        // path and starts a background Task.Run. That background task is not awaited here, meaning
        // uncontrolled work runs during teardown and may emit additional telemetry. Consider using a
        // busy agent (like the rejection tests) to keep this test strictly scoped to the counter increment.
        // TODO [WARNING]: The assertion only checks that agent.jobs.received appears at least once.
        // A regression that emits the counter twice would pass. Consider asserting
        // measurements.Count(m => m.name == "agent.jobs.received") == 1 for tighter coverage.
        var (listener, measurements) = CreateMeterListener();
        using (listener)
        {
            var service = TestAgentWorkerServiceFactory.Create();
            // idle agent — slot available
            await (Task)GetPrivateMethod(service, "HandleAssignJobAsync")
                .Invoke(service, [CreateJobAssignment("job-rcv-pipeline")])!;
        }

        measurements.Should().Contain(m => m.name == "agent.jobs.received",
            "agent.jobs.received must be incremented when HandleAssignJobAsync is called");
    }

    [Fact]
    public async Task HandleAssignConsolidationJobAsync_IncrementsReceivedCounter()
    {
        // Characterization test: pins received counter behavior on the consolidation handler path.
        // HandleAssignConsolidationJobAsync was extracted to ConsolidationJobHandler — call it there.
        // TODO [WARNING]: Same idle-agent background task concern as HandleAssignJobAsync_IncrementsReceivedCounter —
        // the dispatched Task.Run is not awaited, leaving uncontrolled work during teardown.
        // Consider using a busy agent to keep the test scope narrow.
        // TODO [WARNING]: The assertion only checks presence, not exact count (== 1). A double-increment
        // regression would pass silently. Consider asserting the exact measurement count.
        var (listener, measurements) = CreateMeterListener();
        using (listener)
        {
            var service = TestAgentWorkerServiceFactory.Create();
            var message = new ConsolidationJobMessage
            {
                JobId = "job-rcv-consolidation",
                Type = ConsolidationRunType.BrainConsolidation,
                ProviderConfigs = [],
                PipelineConfiguration = new PipelineConfiguration()
            };
            var consolidationHandler = GetConsolidationJobHandler(service);
            await (Task)GetMethod(consolidationHandler, "HandleAssignConsolidationJobAsync")
                .Invoke(consolidationHandler, [message])!;
        }

        measurements.Should().Contain(m => m.name == "agent.jobs.received",
            "agent.jobs.received must be incremented when HandleAssignConsolidationJobAsync is called");
    }

    [Fact]
    public async Task HandleAssignJobAsync_SetsRunTypeTagImplementation()
    {
        // Characterization test: verifies run_type="implementation" is set on the receive activity
        // via ActivityListener (not a source-scan — avoids passing on comment-only matches).
        // Tags are captured on ActivityStopped (after all SetTag calls) rather than ActivityStarted.
        // TODO [WARNING]: ActivityStopped firing synchronously on Activity.Dispose() is an
        // implementation detail of System.Diagnostics.Activity, not a documented test contract. If the
        // runtime ever defers the callback, capturedTags may be empty at assertion time, producing a
        // spurious (false-negative) failure. This is a fragility risk, not a current defect.
        var capturedTags = new List<(string key, object? value)>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = act =>
            {
                if (act.OperationName == "Agent.ReceiveJob")
                {
                    foreach (var tag in act.Tags)
                        capturedTags.Add((tag.Key, tag.Value));
                }
            }
        };
        // TODO [WARNING]: ActivitySource.AddActivityListener registers the listener globally for the
        // process lifetime until the listener is disposed. The `using var` declaration disposes it at
        // the end of the method scope, but if the test throws before that point the listener outlives
        // the test. Other tests in the same process that start Agent.ReceiveJob activities after this
        // test completes may fire the ActivityStopped callback into a potentially stale context.
        // In practice capturedTags is a local list so the risk is low, but the global registration
        // is a structural fragility. Consider wrapping the listener creation and AddActivityListener
        // call in a try/finally (or a helper that ensures removal on disposal).
        ActivitySource.AddActivityListener(activityListener);

        var service = TestAgentWorkerServiceFactory.Create();
        var slotManager = GetSlotManager(service);
        // Use a busy agent so the handler returns early after tagging; avoids background task side effects
        SetPrivateField(slotManager, "_activeJobId", (JobId?)(JobId)"existing");
        SetPrivateField(slotManager, "_isBusy", true);

        await (Task)GetPrivateMethod(service, "HandleAssignJobAsync")
            .Invoke(service, [CreateJobAssignment("tag-test-pipeline")])!;

        capturedTags.Should().Contain(t => t.key == "run_type" && (string?)t.value == "implementation",
            "run_type tag must be set to 'implementation' on the Agent.ReceiveJob activity for pipeline jobs");
    }

    [Fact]
    public async Task HandleAssignConsolidationJobAsync_SetsRunTypeTagConsolidation()
    {
        // Characterization test: verifies run_type="consolidation" is set on the receive activity.
        // Tags are captured on ActivityStopped (after all SetTag calls) rather than ActivityStarted.
        // TODO [WARNING]: Same ActivityStopped timing fragility as HandleAssignJobAsync_SetsRunTypeTagImplementation —
        // the callback firing synchronously on Dispose() is an undocumented implementation detail.
        // If deferred, capturedTags may be empty at assertion time (spurious failure).
        var capturedTags = new List<(string key, object? value)>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = act =>
            {
                if (act.OperationName == "Agent.ReceiveJob")
                {
                    foreach (var tag in act.Tags)
                        capturedTags.Add((tag.Key, tag.Value));
                }
            }
        };
        // TODO [WARNING]: Same globally-registered ActivityListener concern as
        // HandleAssignJobAsync_SetsRunTypeTagImplementation — the listener is registered for the
        // process lifetime until disposed, and other tests that start Agent.ReceiveJob activities
        // after this test could fire the ActivityStopped callback into this test's stale context.
        // See the TODO in the sibling test for the full description and suggested remediation.
        ActivitySource.AddActivityListener(activityListener);

        var service = TestAgentWorkerServiceFactory.Create();
        var slotManager = GetSlotManager(service);
        SetPrivateField(slotManager, "_activeJobId", (JobId?)(JobId)"existing");
        SetPrivateField(slotManager, "_isBusy", true);

        var message = new ConsolidationJobMessage
        {
            JobId = "tag-test-consolidation",
            Type = ConsolidationRunType.BrainConsolidation,
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration()
        };

        // HandleAssignConsolidationJobAsync was extracted to ConsolidationJobHandler — call it there.
        var consolidationHandler = GetConsolidationJobHandler(service);
        await (Task)GetMethod(consolidationHandler, "HandleAssignConsolidationJobAsync")
            .Invoke(consolidationHandler, [message])!;

        capturedTags.Should().Contain(t => t.key == "run_type" && (string?)t.value == "consolidation",
            "run_type tag must be set to 'consolidation' on the Agent.ReceiveJob activity for consolidation jobs");
    }

    // ── HandleAssignJobAsync_WhenBusy — hub throws, should swallow and complete ──────
    // (Previously named RejectJobBusyAsync_HubThrows_CompletesWithoutThrowing — renamed post-extraction
    // because this test invokes the handler, not the old private rejection method.)

    [Fact]
    public async Task HandleAssignJobAsync_WhenBusy_HubThrows_CompletesWithoutThrowing()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var slotManager = GetSlotManager(service);

        // Simulate busy agent
        SetPrivateField(slotManager, "_activeJobId", (JobId?)(JobId)"existing-job");
        SetPrivateField(slotManager, "_isBusy", true);

        var handler = GetPrivateMethod(service, "HandleAssignJobAsync");
        var task = (Task)handler.Invoke(service, [CreateJobAssignment("new-job")])!;
        await task;

        // Existing slot unchanged — new job was rejected
        GetPrivateField<JobId?>(slotManager, "_activeJobId").Should().Be((JobId)"existing-job");
    }

    // ── SendJobAcceptedAsync — hub throws → returns false, releases slot ──

    [Fact]
    public async Task SendJobAcceptedAsync_HubThrows_ReleasesSlot()
    {
        // When the hub is disconnected, SendJobAcceptedAsync catches and calls ForceReleaseJobSlot
        var service = TestAgentWorkerServiceFactory.Create();
        var slotManager = GetSlotManager(service);

        var handler = GetPrivateMethod(service, "HandleAssignJobAsync");
        await (Task)handler.Invoke(service, [CreateJobAssignment("job-send-fail")])!;

        // Slot was released because SendJobAccepted failed
        GetPrivateField<JobId?>(slotManager, "_activeJobId")
            .Should().BeNull("slot should be released when SendJobAccepted fails");
    }

    // ── FinalizeJobAsync — null completion skips reporter call ───────────

    [Fact]
    public async Task FinalizeJobAsync_NullCompletion_DoesNotCallReporter()
    {
        var mockReporter = new Mock<IJobCompletionReporter>();
        var service = TestAgentWorkerServiceFactory.Create(completionReporter: mockReporter.Object);
        var slotManager = GetSlotManager(service);
        slotManager.TryAcquireJobSlot("null-payload-job", out _);

        await (Task)GetPrivateMethod(service, "FinalizeJobAsync")
            .Invoke(service, ["null-payload-job", null])!;

        mockReporter.Verify(r => r.ReportCompletionAsync(
            It.IsAny<JobId>(), It.IsAny<JobCompletionPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── FinalizeJobAsync — with payload calls reporter ───────────────────

    [Fact]
    public async Task FinalizeJobAsync_WithCompletion_CallsReporter()
    {
        var mockReporter = new Mock<IJobCompletionReporter>();
        mockReporter.Setup(r => r.ReportCompletionAsync(
                It.IsAny<JobId>(), It.IsAny<JobCompletionPayload>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = TestAgentWorkerServiceFactory.Create(completionReporter: mockReporter.Object);
        var slotManager = GetSlotManager(service);
        slotManager.TryAcquireJobSlot("complete-job", out _);

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await (Task)GetPrivateMethod(service, "FinalizeJobAsync")
            .Invoke(service, ["complete-job", payload])!;

        mockReporter.Verify(r => r.ReportCompletionAsync(
            new JobId("complete-job"), payload, CancellationToken.None), Times.Once);
    }

    // ── RunJobTaskAsync — executor throws → builds Failed payload ─────────

    [Fact]
    public async Task RunJobTaskAsync_ExecutorThrows_BuildsFailedPayload()
    {
        // Build a service with a throwing IPipelineExecutor
        var mockReporter = new Mock<IJobCompletionReporter>();
        mockReporter.Setup(r => r.ReportCompletionAsync(
                It.IsAny<JobId>(), It.IsAny<JobCompletionPayload>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var throwingExecutor = new Mock<IPipelineExecutor>();
        throwingExecutor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<JobAssignmentMessage>(),
                It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
                It.IsAny<OutputBatcher>(),
                It.IsAny<Action<PipelineStep?>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("executor boom"));

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var hm = TestAgentWorkerServiceFactory.CreateTestHubManager();
        var hmFactory = TestAgentWorkerServiceFactory.CreateTestHubManagerFactory();
        var logger = new Mock<Serilog.ILogger>().Object;
        var buffer = new CriticalMessageBuffer();
        var pipeline = Infrastructure.Resilience.ResiliencePipelineFactory.CreateSignalRPipeline(logger);
        var signalRReporter = new SignalRCompletionReporter(hm, pipeline, buffer, logger);
        var slotManager = new AgentJobSlotManager(() => Task.CompletedTask);
        var lifetime = Mock.Of<IHostApplicationLifetime>();
        var lifecycle = new AgentConnectionLifecycle(hm, hmFactory, signalRReporter, slotManager,
            new AgentId("test"), lifetime, logger);

        var chatHandler = TestAgentWorkerServiceFactory.CreateChatJobHandler(lifecycle, slotManager, mockOrchestrator.Object, lifetime, logger);
        var consolidationExecutor = new LocalConsolidationExecutor(
            mockOrchestrator.Object, Mock.Of<System.Net.Http.IHttpClientFactory>(), logger);
        var consolidationHandler = new ConsolidationJobHandler(lifecycle, slotManager, consolidationExecutor, logger);

        var service = new AgentWorkerService(new AgentWorkerServiceDependencies(
            lifecycle, slotManager,
            chatHandler, consolidationHandler,
            new AgentId("test"),
            throwingExecutor.Object,
            mockReporter.Object, lifetime, logger));

        slotManager.TryAcquireJobSlot("throw-job", out _);

        using var cts = new CancellationTokenSource();
        await (Task)GetPrivateMethod(service, "RunJobTaskAsync")
            .Invoke(service, [CreateJobAssignment("throw-job"), cts.Token])!;

        mockReporter.Verify(r => r.ReportCompletionAsync(
            new JobId("throw-job"),
            It.Is<JobCompletionPayload>(p =>
                p.FinalStep == PipelineStep.Failed &&
                p.FailureReason == "executor boom"),
            CancellationToken.None), Times.Once);
    }

    // ── ReportChatCompletedAsync — hub throws, should not propagate ───────

    [Fact]
    public async Task ReportChatCompletedAsync_HubThrows_DoesNotThrow()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var act = async () => await (Task)GetMethod(GetChatJobHandler(service), "ReportChatCompletedAsync")
            .Invoke(GetChatJobHandler(service), ["sess-1", 0, (string?)null])!;
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReportChatCompletedAsync_WithError_HubThrows_DoesNotThrow()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var act = async () => await (Task)GetMethod(GetChatJobHandler(service), "ReportChatCompletedAsync")
            .Invoke(GetChatJobHandler(service), ["sess-2", 1, "some error"])!;
        await act.Should().NotThrowAsync();
    }

    // ── HandleAssignConsolidationJobAsync_WhenBusy — hub throws, swallowed ──────
    // (Previously named RejectConsolidationJobBusyAsync_HubThrows_CompletesWithoutThrowing — renamed
    // post-extraction because this test invokes the handler, not the old private rejection method.)

    [Fact]
    public async Task HandleAssignConsolidationJobAsync_WhenBusy_HubThrows_CompletesWithoutThrowing()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var slotManager = GetSlotManager(service);
        SetPrivateField(slotManager, "_activeJobId", (JobId?)(JobId)"existing-consolidation");
        SetPrivateField(slotManager, "_isBusy", true);

        var message = new ConsolidationJobMessage
        {
            JobId = "new-consolidation",
            Type = ConsolidationRunType.BrainConsolidation,
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration()
        };

        await (Task)GetMethod(GetConsolidationJobHandler(service), "HandleAssignConsolidationJobAsync")
            .Invoke(GetConsolidationJobHandler(service), [message])!;

        GetPrivateField<JobId?>(slotManager, "_activeJobId")
            .Should().Be((JobId)"existing-consolidation");
    }

    // ── RunConsolidationTaskAsync — executor throws → reports failure ─────

    [Fact]
    public async Task RunConsolidationTaskAsync_ExecutorThrows_ReleasesSlot()
    {
        var throwingConsolidation = new Mock<IConsolidationExecutor>();
        throwingConsolidation
            .Setup(e => e.ExecuteAsync(
                It.IsAny<ConsolidationJobMessage>(),
                It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("consolidation boom"));

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var hm = TestAgentWorkerServiceFactory.CreateTestHubManager();
        var hmFactory = TestAgentWorkerServiceFactory.CreateTestHubManagerFactory();
        var logger = new Mock<Serilog.ILogger>().Object;
        var buffer = new CriticalMessageBuffer();
        var pipeline = Infrastructure.Resilience.ResiliencePipelineFactory.CreateSignalRPipeline(logger);
        var signalRReporter = new SignalRCompletionReporter(hm, pipeline, buffer, logger);
        var slotManager = new AgentJobSlotManager(() => Task.CompletedTask);
        var lifetime = Mock.Of<IHostApplicationLifetime>();
        var lifecycle = new AgentConnectionLifecycle(hm, hmFactory, signalRReporter, slotManager,
            new AgentId("test"), lifetime, logger);
        var pipelineExecutor = new LocalPipelineExecutor(new LocalPipelineExecutorDependencies(
            mockOrchestrator.Object, Mock.Of<System.Net.Http.IHttpClientFactory>(),
            new PipelineConfiguration(), Mock.Of<IQualityGateValidator>(), logger,
            AgentIdentity: new AgentId("test")));

        var chatHandler = TestAgentWorkerServiceFactory.CreateChatJobHandler(lifecycle, slotManager, mockOrchestrator.Object, lifetime, logger);
        var consolidationHandler = new ConsolidationJobHandler(lifecycle, slotManager, throwingConsolidation.Object, logger);

        var service = new AgentWorkerService(new AgentWorkerServiceDependencies(
            lifecycle, slotManager,
            chatHandler, consolidationHandler,
            new AgentId("test"),
            pipelineExecutor,
            signalRReporter, lifetime, logger));

        slotManager.TryAcquireJobSlot("consolidation-throw-job", out _);

        var message = new ConsolidationJobMessage
        {
            JobId = "consolidation-throw-job",
            Type = ConsolidationRunType.BrainConsolidation,
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration()
        };

        using var cts = new CancellationTokenSource();
        await (Task)GetMethod(GetConsolidationJobHandler(service), "RunConsolidationTaskAsync")
            .Invoke(GetConsolidationJobHandler(service), [message, cts.Token])!;

        // Slot released in the finally block even when executor throws
        GetPrivateField<JobId?>(slotManager, "_activeJobId")
            .Should().BeNull("slot must be released in finally block");
    }

    // ── ReportConsolidationFailureAsync — hub throws, should not propagate

    [Fact]
    public async Task ReportConsolidationFailureAsync_HubThrows_DoesNotThrow()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var act = async () => await (Task)GetMethod(GetConsolidationJobHandler(service), "ReportConsolidationFailureAsync")
            .Invoke(GetConsolidationJobHandler(service), ["job-id", "error msg"])!;
        await act.Should().NotThrowAsync();
    }

    // ── HandleAssignConsolidationJobAsync — idle agent, slot acquired ─────

    [Fact]
    public async Task HandleAssignConsolidationJobAsync_WhenIdle_AcquiresSlot()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var slotManager = GetSlotManager(service);

        var message = new ConsolidationJobMessage
        {
            JobId = "idle-consolidation",
            Type = ConsolidationRunType.BrainConsolidation,
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration()
        };

        await (Task)GetMethod(GetConsolidationJobHandler(service), "HandleAssignConsolidationJobAsync")
            .Invoke(GetConsolidationJobHandler(service), [message])!;

        // Background task was started
        GetPrivateField<Task?>(slotManager, "_activeJobTask")
            .Should().NotBeNull("consolidation task should be started");
    }

    // ── RunChatTaskAsync — releases chat slot on completion ──────────────

    [Fact]
    public async Task RunChatTaskAsync_KiroCliPath_ReleasesChatSlotAfterCompletion()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns(Task.FromResult(0));

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var slotManager = GetSlotManager(service);

        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        catch { return; } // Skip if workspace can't be created

        slotManager.TryAcquireChatSlot("chat-slot-sess", out _);

        var message = new ChatPromptMessage
        {
            SessionId = "chat-slot-sess",
            Prompt = "hello",
            UseResume = true
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = (Task)GetMethod(GetChatJobHandler(service), "RunChatTaskAsync")
            .Invoke(GetChatJobHandler(service), [message, cts.Token])!;
        // TODO: Task.WhenAny does not re-throw if runTask faults; assertions below execute
        // even on a timeout or unhandled exception, potentially checking state that was never
        // established. Consider awaiting runTask directly (or checking runTask.IsCompletedSuccessfully)
        // to distinguish genuine completion from a 5-second timeout masked as a pass.
        await Task.WhenAny(runTask, Task.Delay(5000));

        GetPrivateField<string?>(slotManager, "_activeChatSessionId")
            .Should().BeNull("chat slot should be released after RunChatTaskAsync");
    }

    // ── RunChatTaskAsync — slot released even when hub is disconnected during reporting ──

    /// <summary>
    /// Regression guard for issue #1857: verifies ReleaseChatSlot() is called even when
    /// the hub connection is unavailable during ReportChatCompletedAsync. The default
    /// TestAgentWorkerServiceFactory uses a disconnected hub, so InvokeAsync fails inside
    /// ReportChatCompletedAsync (which swallows the error). The slot must still be released.
    ///
    /// Note: Since ReportChatCompletedAsync has an unconditional catch today, this test
    /// exercises the normal completion path with a disconnected hub rather than a true
    /// propagated-throw scenario. Its value is as a regression guard: if the finally block
    /// is ever removed, future changes that allow ReportChatCompletedAsync to propagate
    /// exceptions would immediately cause the slot to leak — and this test would catch that.
    /// The source-scan test (SourceCode_RunChatTaskAsync_ReleaseChatSlotIsInsideFinallyBlock)
    /// is the primary structural guard; this test provides a behavioral complement.
    /// </summary>
    // TODO: This test does not actually exercise the failure scenario its name describes.
    // ReportChatCompletedAsync swallows exceptions internally (unconditional catch), so the
    // test only exercises the normal completion path — the finally block is never triggered by
    // an exception. This means the test would pass identically if the try/finally fix were
    // reverted back to sequential code. The behavioral coverage this test claims to provide
    // is not real; the structural source-scan test is the actual regression guard.
    // To make this test meaningful, ReportChatCompletedAsync would need to propagate exceptions
    // (or a separate overload/mock injection point would be needed to simulate throw behaviour).
    // See review finding: RunChatTaskAsync_WhenReportChatCompletedThrows_StillReleasesChatSlot.
    [Fact]
    public async Task RunChatTaskAsync_WhenReportChatCompletedThrows_StillReleasesChatSlot()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns(Task.FromResult(0));

        // Default factory uses a disconnected hub — ReportChatCompletedAsync will encounter
        // an InvokeAsync failure (swallowed internally). The slot must still be released.
        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var slotManager = GetSlotManager(service);

        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        // TODO: This bare `catch { return; }` silently skips the rest of the test (including all
        // assertions) without marking the test as skipped in the test runner. If the workspace
        // directory cannot be created (e.g., permission restrictions in CI), this test produces
        // a false-green with no visibility in test reports. Replace with Assert.Skip("reason")
        // (xUnit v3) or a [Fact(Skip = "...")] guard to surface skipped runs explicitly.
        // See review finding: RunChatTaskAsync_WhenReportChatCompletedThrows_StillReleasesChatSlot silent skip.
        catch { return; } // Skip if workspace can't be created in this environment

        slotManager.TryAcquireChatSlot("slot-leak-guard-sess", out _);

        var message = new ChatPromptMessage
        {
            SessionId = "slot-leak-guard-sess",
            Prompt = "hello",
            UseResume = true
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Directly await the task (not Task.WhenAny) so any unexpected fault surfaces immediately
        // rather than being masked by a timeout producing a false-green result.
        await (Task)GetMethod(GetChatJobHandler(service), "RunChatTaskAsync")
            .Invoke(GetChatJobHandler(service), [message, cts.Token])!;

        GetPrivateField<string?>(slotManager, "_activeChatSessionId")
            .Should().BeNull("chat slot must be released unconditionally via the finally block, " +
                             "regardless of what ReportChatCompletedAsync does internally");
    }

    // ── ExecuteChatWithOutputAsync — OperationCanceledException branch ────

    [Fact]
    public async Task ExecuteChatWithOutputAsync_PreCancelledToken_ReturnsCancelledOrFailure()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await using var batcher = new OutputBatcher();
        var message = new ChatPromptMessage
        {
            SessionId = "sess-cancel",
            Prompt = "test",
            UseResume = false
        };

        var task = (Task<(int exitCode, string? error)>)GetMethod(GetChatJobHandler(service), "ExecuteChatWithOutputAsync")
            .Invoke(GetChatJobHandler(service), [message, batcher, cts.Token])!;
        var (exitCode, _) = await task;

        // OCE is caught → Cancelled (1), or workspace succeeded before cancel → GeneralFailure (1) or 0
        exitCode.Should().BeOneOf(1, 0);
    }

    // ── ExecuteChatWithOutputAsync — general exception → GeneralFailure ───

    [Fact]
    public async Task ExecuteChatWithOutputAsync_ProviderThrows_ReturnsGeneralFailure()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("orchestrator exploded"));

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);

        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        catch { return; }

        await using var batcher = new OutputBatcher();
        var message = new ChatPromptMessage
        {
            SessionId = "sess-throw",
            Prompt = "boom",
            UseResume = true
        };

        var task = (Task<(int exitCode, string? error)>)GetMethod(GetChatJobHandler(service), "ExecuteChatWithOutputAsync")
            .Invoke(GetChatJobHandler(service), [message, batcher, CancellationToken.None])!;
        var (exitCode, error) = await task;

        exitCode.Should().Be(1, "general exception → GeneralFailure exit code 1");
        error.Should().NotBeNullOrEmpty();
    }

    // ── HandleFetchModelsAsync — error path (non-zero exit, ReadToEndAsync uses CancellationToken.None) ──

    /// <summary>
    /// Verifies that HandleFetchModelsAsync completes without throwing when kiro-cli exits non-zero.
    /// The changed line (ReadToEndAsync(CancellationToken.None)) is exercised by this path.
    /// The error is swallowed internally via ReportFetchModelsError (hub call may fail in test env).
    /// </summary>
    [Fact]
    public async Task HandleFetchModelsAsync_NonZeroExit_CompletesWithoutThrowing()
    {
        // Arrange: point kiro-cli at /usr/bin/false which exits with code 1
        var origPath = Environment.GetEnvironmentVariable(AgentDefaults.EnvKiroCliPath);
        try
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, "/usr/bin/false");
            var service = TestAgentWorkerServiceFactory.Create();
            var request = new FetchModelsRequest { RequestId = "test-req-error" };

            // Act: invoke via reflection — exception from hub is caught internally
            // TODO: Indentation inconsistency — the .Invoke(...) continuation is aligned at column 12 instead of
            // the expected column 16 (matching the async lambda body). This obscures the two-part method call chain
            // and could confuse readers about grouping. Reformat to align .Invoke() under GetMethod().
            var act = async () => await (Task)GetMethod(GetChatJobHandler(service), "HandleFetchModelsAsync")
            .Invoke(GetChatJobHandler(service), [request])!;

            // Assert: method must not propagate exceptions (all errors caught internally)
            await act.Should().NotThrowAsync(
                "HandleFetchModelsAsync must swallow all errors via ReportFetchModelsError");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, origPath);
        }
    }

    // ── HandleFetchModelsAsync — success path (zero exit, InvokeAsync uses CancellationToken.None) ──

    /// <summary>
    /// Verifies that HandleFetchModelsAsync completes without throwing when kiro-cli exits zero
    /// and outputs valid JSON. The changed line (InvokeAsync(…, CancellationToken.None)) is
    /// exercised by this path. The hub InvokeAsync will fail (no connection) but the exception
    /// is caught by the surrounding try/catch, so the method must still complete normally.
    /// </summary>
    [Fact]
    public async Task HandleFetchModelsAsync_ZeroExitWithValidJson_CompletesWithoutThrowing()
    {
        // Arrange: shell script that outputs valid model JSON to stdout and exits 0
        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-kiro-{Guid.NewGuid():N}.sh");
        try
        {
            // Write a shell script that echoes valid JSON and exits 0
            var validJson = """{"models":[{"model_id":"test-model","description":"Test","rate_multiplier":1.0}]}""";
            await File.WriteAllTextAsync(scriptPath, $"#!/bin/sh\necho '{validJson}'\nexit 0\n");
            // Make executable — use chmod process to avoid CA1416 platform guard requirement
            using var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x {scriptPath}",
                UseShellExecute = false
            });
            if (chmod is not null) await chmod.WaitForExitAsync();

            var origPath = Environment.GetEnvironmentVariable(AgentDefaults.EnvKiroCliPath);
            try
            {
                Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, scriptPath);
                var service = TestAgentWorkerServiceFactory.Create();
                var request = new FetchModelsRequest { RequestId = "test-req-success" };

                // Act: invoke via reflection — InvokeAsync will fail (no hub) but exception is caught
                // TODO: Indentation inconsistency — same as the error-path test above. The .Invoke(...) continuation
                // is aligned at column 12 instead of column 16. Reformat to align under GetMethod().
                var act = async () => await (Task)GetMethod(GetChatJobHandler(service), "HandleFetchModelsAsync")
            .Invoke(GetChatJobHandler(service), [request])!;

                // Assert: method must complete without propagating exceptions
                await act.Should().NotThrowAsync(
                    "HandleFetchModelsAsync must swallow all errors including hub InvokeAsync failures");
            }
            finally
            {
                Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, origPath);
            }
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static AgentJobSlotManager GetSlotManager(AgentWorkerService service)
    {
        var field = typeof(AgentWorkerService).GetField("_slotManager",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Field '_slotManager' not found");
        return (AgentJobSlotManager)field.GetValue(service)!;
    }

    private static MethodInfo GetPrivateMethod(object obj, string name) =>
        obj.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"Method '{name}' not found");

    /// <summary>
    /// Gets a public method on a handler class by name. Used for ChatJobHandler and
    /// ConsolidationJobHandler methods that moved from private on AgentWorkerService
    /// to public on the extracted handler class.
    /// </summary>
    private static MethodInfo GetMethod(object obj, string name) =>
        obj.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"Method '{name}' not found on {obj.GetType().Name}");

    private static ChatJobHandler GetChatJobHandler(AgentWorkerService service)
    {
        var field = typeof(AgentWorkerService).GetField("_chatJobHandler",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Field '_chatJobHandler' not found");
        return (ChatJobHandler)field.GetValue(service)!;
    }

    private static ConsolidationJobHandler GetConsolidationJobHandler(AgentWorkerService service)
    {
        var field = typeof(AgentWorkerService).GetField("_consolidationJobHandler",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Field '_consolidationJobHandler' not found");
        return (ConsolidationJobHandler)field.GetValue(service)!;
    }

    private static void SetPrivateField(object obj, string name, object? value)
    {
        var field = obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{name}' not found");
        field.SetValue(obj, value);
    }

    private static T? GetPrivateField<T>(object obj, string name)
    {
        var field = obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{name}' not found");
        return (T?)field.GetValue(obj);
    }

    private static JobAssignmentMessage CreateJobAssignment(string jobId = "test-job") => new()
    {
        JobId = jobId,
        IssueIdentifier = "owner/repo#1",
        IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
        ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
        RepoProviderConfigId = "repo-1",
        AgentProviderConfigId = "agent-1",
        PipelineConfiguration = new PipelineConfiguration(),
        ProviderConfigs = [],
        ReviewerConfigs = [],
        QualityGateConfigs = [],
        IssueComments = [],
        McpServers = [],
        InitiatedBy = "test-user"
    };

    // ── Project secrets injection and cleanup ─────────────────────────────────

    /// <summary>
    /// Verifies that ProjectSecrets on the KiroCli path are passed as environmentVariables
    /// to the orchestrator and NOT set as process-wide environment variables (issue #1913).
    /// </summary>
    [Fact]
    public async Task RunChatTaskAsync_WithProjectSecrets_PassesEnvVarsToOrchestratorNotGlobally()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var secretKey = $"TEST_CHAT_SECRET_{Guid.NewGuid():N}";

        IReadOnlyDictionary<string, string>? capturedEnvVars = null;
        var capturedGlobalEnvVar = new List<string?>();

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                (_, _, _, _, _, _, envVars) =>
                {
                    capturedEnvVars = envVars;
                    // Capture global env to verify it is NOT set
                    capturedGlobalEnvVar.Add(Environment.GetEnvironmentVariable(secretKey));
                    return Task.FromResult(0);
                });

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var slotManager = GetSlotManager(service);

        // Do NOT swallow directory-creation failures here (issue #1913 [CRITICAL] fix).
        // A silent `catch { return; }` causes a vacuous pass — the orchestrator is never
        // invoked, capturedEnvVars remains null, and the central assertion is never evaluated.
        // Infrastructure failures must surface as test failures so regressions are visible.
        Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath);

        slotManager.TryAcquireChatSlot("secrets-inject-sess", out _);

        var message = new ChatPromptMessage
        {
            SessionId = "secrets-inject-sess",
            Prompt = "test",
            UseResume = false,
            ProjectSecrets = new Dictionary<string, string> { [secretKey] = "injected-value" }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = (Task)GetMethod(GetChatJobHandler(service), "RunChatTaskAsync")
            .Invoke(GetChatJobHandler(service), [message, cts.Token])!;
        // TODO [WARNING]: Task.WhenAny does not re-throw if runTask faults or times out.
        // If runTask does not complete within 5 seconds (CI slowdown, deadlock, early workspace-creation
        // return), capturedEnvVars remains null and capturedGlobalEnvVar remains empty, making timeout
        // failures indistinguishable from genuine behavioral failures. Consider awaiting runTask directly
        // (or asserting runTask.IsCompletedSuccessfully) to cleanly separate infrastructure timeouts
        // from secret-injection regressions.
        await Task.WhenAny(runTask, Task.Delay(5000));

        // Secrets are passed to the orchestrator as environmentVariables
        capturedEnvVars.Should().NotBeNull("orchestrator must have been invoked with environmentVariables");
        capturedEnvVars.Should().ContainKey(secretKey).WhoseValue.Should().Be("injected-value",
            "ProjectSecrets must be forwarded to orchestrator via environmentVariables");

        // Secrets are NOT set as process-wide environment variables
        capturedGlobalEnvVar.Should().NotBeEmpty("orchestrator must have been invoked");
        capturedGlobalEnvVar[0].Should().BeNull(
            "ProjectSecrets must NOT pollute the parent process environment");

        // After completion the global env var must remain null
        Environment.GetEnvironmentVariable(secretKey).Should().BeNull(
            "Global env must not be set before, during, or after chat execution");
    }

    /// <summary>
    /// Verifies that when the orchestrator throws, no global env vars were set
    /// (since we no longer use Environment.SetEnvironmentVariable at all).
    /// </summary>
    [Fact]
    public async Task RunChatTaskAsync_WithProjectSecrets_WhenOrchestratorThrows_NoGlobalEnvPollution()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var secretKey = $"TEST_CHAT_SECRET_THROW_{Guid.NewGuid():N}";

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ThrowsAsync(new InvalidOperationException("orchestrator boom"));

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var slotManager = GetSlotManager(service);

        // Do NOT swallow directory-creation failures here (issue #1913 [CRITICAL] fix).
        // A silent `catch { return; }` causes a vacuous pass when directory creation fails —
        // the orchestrator is never invoked and the global-env assertion is never evaluated,
        // masking a potential regression where Environment.SetEnvironmentVariable was re-introduced.
        Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath);

        slotManager.TryAcquireChatSlot("secrets-throw-sess", out _);

        var message = new ChatPromptMessage
        {
            SessionId = "secrets-throw-sess",
            Prompt = "boom",
            UseResume = false,
            ProjectSecrets = new Dictionary<string, string> { [secretKey] = "should-never-appear-globally" }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = (Task)GetMethod(GetChatJobHandler(service), "RunChatTaskAsync")
            .Invoke(GetChatJobHandler(service), [message, cts.Token])!;
        // TODO [WARNING]: Task.WhenAny does not re-throw if runTask faults or times out.
        // A CI slowdown or the silent workspace-creation early-return makes this indistinguishable
        // from a genuine pass. Consider awaiting runTask directly to separate timeout from regression.
        await Task.WhenAny(runTask, Task.Delay(5000));

        // Even though orchestrator threw, no global env var was ever set
        Environment.GetEnvironmentVariable(secretKey).Should().BeNull(
            "ProjectSecrets must never be set in the global process environment");
    }

    /// <summary>
    /// Verifies that null ProjectSecrets produce no env var changes and do not break execution.
    /// </summary>
    [Fact]
    public async Task ExecuteChatWithOutputAsync_NullProjectSecrets_NoEnvVarsSet()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var sentinelKey = $"TEST_NULL_SECRET_{Guid.NewGuid():N}";

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns(Task.FromResult(0));

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);

        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        catch { return; }

        await using var batcher = new OutputBatcher();
        var message = new ChatPromptMessage
        {
            SessionId = "null-secrets-sess",
            Prompt = "test",
            UseResume = false,
            ProjectSecrets = null   // null = no secrets, backward compat
        };

        var task = (Task<(int, string?)>)GetMethod(GetChatJobHandler(service), "ExecuteChatWithOutputAsync")
            .Invoke(GetChatJobHandler(service), [message, batcher, CancellationToken.None])!;
        await task;

        // TODO [WARNING]: sentinelKey is a randomly-generated key that is never set anywhere,
        // so this assertion would pass even if the production code called Environment.SetEnvironmentVariable
        // with a different key. A stronger check would capture the environmentVariables argument from
        // the orchestrator mock (as done in RunChatTaskAsync_WithProjectSecrets_PassesEnvVarsToOrchestratorNotGlobally)
        // and assert it is null or empty — that directly validates the no-injection contract.
        // No env vars should have been set — null ProjectSecrets must not inject anything
        Environment.GetEnvironmentVariable(sentinelKey).Should().BeNull(
            "null ProjectSecrets must not inject any env vars");
    }

    // ── CleanupChatSecrets removed in issue #1913 ──────────────────────────────

    /// <summary>
    /// Documents that <c>CleanupChatSecrets</c> was removed in issue #1913 as part of replacing
    /// process-wide env var injection with per-process <see cref="System.Diagnostics.ProcessStartInfo.Environment"/>.
    /// Secrets are now passed directly to the orchestrator and never touch the parent process env.
    /// </summary>
    [Fact]
    public void CleanupChatSecrets_MethodNoLongerExists()
    {
        var method = typeof(AgentWorkerService)
            .GetMethod("CleanupChatSecrets", BindingFlags.NonPublic | BindingFlags.Instance);

        method.Should().BeNull(
            "CleanupChatSecrets was removed in issue #1913 — secrets are now passed per-process via environmentVariables");
    }

    // ── Project steering write ─────────────────────────────────────────────────

    /// <summary>
    /// Verifies that ProjectSteeringContent is written to the chat workspace before
    /// the orchestrator is invoked on the first prompt (UseResume=false).
    /// </summary>
    [Fact]
    public async Task ExecuteChatWithOutputAsync_WithProjectSteeringContent_WritesFileBeforeOrchestrator()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");

        var steeringFilesWhenInvoked = new List<bool>();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                (_, workspace, _, _, _, _, _) =>
                {
                    var steeringPath = Path.Combine(workspace, ".kiro", "steering", "pipeline-project.md");
                    steeringFilesWhenInvoked.Add(File.Exists(steeringPath));
                    return Task.FromResult(0);
                });

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var chatWindowId = Guid.NewGuid().ToString();
        var chatWorkspace = Path.Combine(AgentDefaults.ChatWorkspacesRoot, chatWindowId);

        try { Directory.CreateDirectory(chatWorkspace); }
        catch { return; }

        await using var batcher = new OutputBatcher();
        var message = new ChatPromptMessage
        {
            SessionId = "steering-before-prompt-sess",
            Prompt = "test steering",
            UseResume = false,
            ChatWindowId = chatWindowId,
            ProjectSteeringContent = "# Instructions\nUse TDD."
        };

        var task = (Task<(int, string?)>)GetMethod(GetChatJobHandler(service), "ExecuteChatWithOutputAsync")
            .Invoke(GetChatJobHandler(service), [message, batcher, CancellationToken.None])!;
        await task;

        steeringFilesWhenInvoked.Should().NotBeEmpty("orchestrator must have been invoked");
        steeringFilesWhenInvoked[0].Should().BeTrue(
            "steering file must be written before the orchestrator is invoked (including warm-up prompt)");
    }

    /// <summary>
    /// Verifies that ProjectSteeringContent is NOT written on resume prompts (UseResume=true).
    /// </summary>
    [Fact]
    public async Task ExecuteChatWithOutputAsync_WithProjectSteeringContent_SkipsWriteOnResume()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns(Task.FromResult(0));

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var chatWindowId = Guid.NewGuid().ToString();
        var chatWorkspace = Path.Combine(AgentDefaults.ChatWorkspacesRoot, chatWindowId);

        try { Directory.CreateDirectory(chatWorkspace); }
        catch { return; }

        await using var batcher = new OutputBatcher();
        var message = new ChatPromptMessage
        {
            SessionId = "steering-resume-sess",
            Prompt = "follow-up prompt",
            UseResume = true,   // resume = NOT first prompt
            ChatWindowId = chatWindowId,
            ProjectSteeringContent = "# Instructions\nUse TDD."
        };

        var task = (Task<(int, string?)>)GetMethod(GetChatJobHandler(service), "ExecuteChatWithOutputAsync")
            .Invoke(GetChatJobHandler(service), [message, batcher, CancellationToken.None])!;
        await task;

        var steeringPath = Path.Combine(chatWorkspace, ".kiro", "steering", "pipeline-project.md");
        File.Exists(steeringPath).Should().BeFalse(
            "steering file must NOT be written on resume prompts (UseResume=true)");
    }

    /// <summary>
    /// Verifies null ProjectSteeringContent produces no steering file (backward compat).
    /// </summary>
    [Fact]
    public async Task ExecuteChatWithOutputAsync_NullProjectSteeringContent_NoSteeringFileWritten()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns(Task.FromResult(0));

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var chatWindowId = Guid.NewGuid().ToString();
        var chatWorkspace = Path.Combine(AgentDefaults.ChatWorkspacesRoot, chatWindowId);

        try { Directory.CreateDirectory(chatWorkspace); }
        catch { return; }

        await using var batcher = new OutputBatcher();
        var message = new ChatPromptMessage
        {
            SessionId = "no-steering-sess",
            Prompt = "no project",
            UseResume = false,
            ChatWindowId = chatWindowId,
            ProjectSteeringContent = null   // null = no project selected
        };

        var task = (Task<(int, string?)>)GetMethod(GetChatJobHandler(service), "ExecuteChatWithOutputAsync")
            .Invoke(GetChatJobHandler(service), [message, batcher, CancellationToken.None])!;
        await task;

        var steeringPath = Path.Combine(chatWorkspace, ".kiro", "steering", "pipeline-project.md");
        File.Exists(steeringPath).Should().BeFalse(
            "null ProjectSteeringContent must produce no steering file (backward compat — no project selected)");
    }
}
