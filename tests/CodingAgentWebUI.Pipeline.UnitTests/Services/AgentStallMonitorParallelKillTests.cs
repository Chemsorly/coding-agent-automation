using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Tests that demonstrate the stall monitor's inability to kill ephemeral (parallel)
/// agent processes. During parallel review execution, each agent creates an ephemeral
/// orchestrator inside ExecuteAsync, but the stall monitor calls GetHealthStatus() on
/// the shared IAgentProvider — which reads from the shared orchestrator (no active process).
///
/// Bug: GetHealthStatus().IsProcessAlive returns null (no process on shared orchestrator),
/// so the "process dead" check doesn't trigger. When silence exceeds killTimeout, the
/// monitor calls KillAsync() on the shared provider, which is a no-op because the shared
/// orchestrator has no active process. The ephemeral process continues running forever.
/// </summary>
public class AgentStallMonitorParallelKillTests
{
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;

    public AgentStallMonitorParallelKillTests()
    {
        _mockAgent = new Mock<IAgentProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();
        _run = new PipelineRun
        {
            RunId = "parallel-review-run",
            IssueIdentifier = "679",
            IssueTitle = "Decompose god component",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp"
        };
    }

    /// <summary>
    /// Reproduces the production bug: when the shared orchestrator has no active process
    /// (because the real process runs on an ephemeral orchestrator created inside ExecuteAsync),
    /// GetHealthStatus returns IsProcessAlive=null and LastOutputTime=null.
    ///
    /// The stall monitor's kill fires (silence exceeds timeout) but KillAsync is called on
    /// the shared provider — which has nothing to kill. The agent call continues forever
    /// because the ephemeral process is not terminated.
    ///
    /// Expected behavior: KillAsync should be invoked AND should actually terminate the
    /// running process. This test demonstrates the bug by showing that even after KillAsync
    /// is called, the agent execution is NOT cancelled (the task never completes on its own).
    /// </summary>
    [Fact]
    public async Task WhenSharedOrchestratorHasNoProcess_KillAsyncFiresButDoesNotTerminateEphemeralProcess()
    {
        var config = new PipelineConfiguration
        {
            StallPollInterval = TimeSpan.FromMilliseconds(30),
            StallWarningInterval = TimeSpan.FromHours(1), // suppress warnings for clarity
            AgentTimeout = TimeSpan.FromMilliseconds(100) // short kill timeout for fast test
        };

        // Simulate the parallel execution scenario:
        // - Shared orchestrator has NO active process (IsProcessAlive = null)
        // - LastOutputTime = null (no process means no output tracking)
        // This is exactly what happens when ephemeral orchestrators are used
        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus
            {
                IsExecuting = false,   // shared orchestrator is idle
                ProcessId = null,      // no process on shared orchestrator
                IsProcessAlive = null, // null, NOT false — this bypasses the "process dead" check
                LastOutputTime = null  // no output tracked on shared orchestrator
            });

        // The agent call hangs forever (simulating a stuck kiro-cli process on ephemeral orchestrator)
        var agentCallCts = new CancellationTokenSource();
        var agentCallStarted = new TaskCompletionSource();
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(async (AgentRequest _, CancellationToken ct, Action<string>? _) =>
            {
                agentCallStarted.SetResult();
                // This simulates the ephemeral process hanging — it only completes
                // if externally cancelled (which the stall monitor's KillAsync doesn't achieve
                // because it kills the shared orchestrator, not the ephemeral one)
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException) { }
                return new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() };
            });

        // KillAsync on the shared provider is called but doesn't cancel the ephemeral process
        var killCalled = new TaskCompletionSource();
        _mockAgent.Setup(a => a.KillAsync()).Returns(() =>
        {
            killCalled.SetResult();
            // NOTE: This does NOT cancel the agent execution because it only kills
            // the shared orchestrator's (non-existent) process, not the ephemeral one.
            return Task.CompletedTask;
        });

        // Start monitoring
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // safety timeout
        var monitorTask = AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "review prompt", WorkspacePath = "/ws", Timeout = TimeSpan.FromHours(2) },
            _run, config, "Code review agent 'Correctness'", null, _mockLogger.Object, cts.Token);

        // Wait for the agent call to start
        await agentCallStarted.Task;

        // Wait for KillAsync to be called by the stall monitor
        var killCalledInTime = await Task.WhenAny(killCalled.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        killCalledInTime.Should().Be(killCalled.Task,
            "the stall monitor should call KillAsync after silence exceeds the kill timeout");

        // CRITICAL ASSERTION: After KillAsync fires, the monitor task should ideally complete
        // (because the stuck process was killed). But due to the bug, the agent call continues
        // running because KillAsync only targets the shared orchestrator (no-op).
        //
        // The monitor loop breaks after calling KillAsync, but the ExecuteWithMonitoringAsync
        // awaits agentProvider.ExecuteAsync which is still hanging. The only way it completes
        // is via the CancellationToken (our safety timeout).
        var completedInTime = await Task.WhenAny(monitorTask, Task.Delay(TimeSpan.FromMilliseconds(500)));

        // BUG DEMONSTRATION: The task does NOT complete within 500ms after kill,
        // because the ephemeral process is still running (KillAsync was a no-op).
        // In a fixed implementation, the kill should propagate cancellation to the agent call.
        completedInTime.Should().NotBe(monitorTask,
            "BUG: agent execution continues after KillAsync because the shared provider's kill " +
            "does not reach the ephemeral orchestrator running the actual CLI process");

        // Verify KillAsync was called exactly once
        _mockAgent.Verify(a => a.KillAsync(), Times.Once);

        // Verify the kill message was logged to chat history
        _run.ChatHistory.Should().Contain(c =>
            c.Role == ChatRole.System &&
            c.Content.Contains("Forcefully terminating agent process"));

        // Clean up: cancel to unblock the hanging task
        await cts.CancelAsync();
        try { await monitorTask; }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Verifies that when IsProcessAlive is null (no process on shared orchestrator),
    /// the stall monitor does NOT trigger the "process dead" early exit. It falls through
    /// to the silence-based kill path instead.
    ///
    /// This confirms the monitor's guard `if (health.IsProcessAlive == false)` uses strict
    /// equality, so null (unknown) is treated as "still alive" — which is correct behavior
    /// for the case where we genuinely don't know, but problematic during parallel execution
    /// where null means "no process on the shared orchestrator we're monitoring."
    /// </summary>
    [Fact]
    public async Task WhenIsProcessAliveIsNull_DoesNotTriggerProcessDeadDetection()
    {
        var config = new PipelineConfiguration
        {
            StallPollInterval = TimeSpan.FromMilliseconds(30),
            StallWarningInterval = TimeSpan.FromHours(1), // long warning interval so no warning fires
            AgentTimeout = TimeSpan.FromHours(24) // extremely long kill timeout so it cannot fire
        };

        // Null IsProcessAlive — exactly what happens during parallel execution
        // With recent LastOutputTime, neither warning nor kill should fire
        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus
            {
                IsExecuting = false,
                ProcessId = null,
                IsProcessAlive = null,     // null != false, so "process dead" check passes
                LastOutputTime = DateTime.UtcNow // prevent silence-based kill by providing recent output time
            });

        _mockAgent.Setup(a => a.KillAsync()).Returns(Task.CompletedTask);

        var tcs = new TaskCompletionSource<AgentResult>();
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(tcs.Task);

        var task = AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "test", WorkspacePath = "/ws" },
            _run, config, "Parallel review agent", null, _mockLogger.Object, CancellationToken.None);

        // Give the monitor time to run a few poll cycles
        await Task.Delay(200);

        // Complete the agent call normally
        tcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        var result = await task;

        // Process was never killed — null IsProcessAlive bypasses the early exit
        _mockAgent.Verify(a => a.KillAsync(), Times.Never);
        result.ExitCode.Should().Be(0);

        // No silence warning because LastOutputTime is recent
        _run.ChatHistory.Should().BeEmpty();
    }

    /// <summary>
    /// Demonstrates that when LastOutputTime is null and the run started a long time ago,
    /// the silence calculation uses run.StartedAt as the reference, causing immediate
    /// large silence values that can trigger the kill even if the process just started.
    ///
    /// In parallel mode, this means a brand-new ephemeral process inherits the entire
    /// run duration as its "silence" because the shared orchestrator has no LastOutputTime.
    /// </summary>
    [Fact]
    public async Task WhenLastOutputTimeNull_FallsBackToRunStartedAt_CausesImmediateLargeSilence()
    {
        // Simulate a run that started 90 minutes ago (common for code gen phase completion)
        var run = new PipelineRun
        {
            RunId = "parallel-review-run-2",
            IssueIdentifier = "679",
            IssueTitle = "Decompose god component",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow.AddMinutes(-90)
        };

        var config = new PipelineConfiguration
        {
            StallPollInterval = TimeSpan.FromMilliseconds(30),
            StallWarningInterval = TimeSpan.FromHours(10), // suppress repeated warnings
            AgentTimeout = TimeSpan.FromMinutes(60) // kill timeout: 60 min
        };

        // Shared orchestrator returns null LastOutputTime (parallel mode)
        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus
            {
                IsExecuting = false,
                ProcessId = null,
                IsProcessAlive = null,
                LastOutputTime = null
            });

        var tcs = new TaskCompletionSource<AgentResult>();
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(tcs.Task);
        _mockAgent.Setup(a => a.KillAsync()).Returns(Task.CompletedTask);

        var task = AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "review", WorkspacePath = "/ws" },
            run, config, "Code review agent 'AcceptanceCriteria'", null, _mockLogger.Object, CancellationToken.None);

        // The kill should fire almost immediately because:
        // silence = now - run.StartedAt = 90 minutes > killTimeout (60 minutes)
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (run.ChatHistory.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(30);

        tcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await task;

        // Kill was triggered immediately due to inherited silence from run start
        _mockAgent.Verify(a => a.KillAsync(), Times.Once);
        run.ChatHistory.Should().Contain(c =>
            c.Role == ChatRole.System &&
            c.Content.Contains("Forcefully terminating agent process") &&
            c.Content.Contains("AcceptanceCriteria"));
    }
}
