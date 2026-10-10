using System.Diagnostics;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// The agent, run and phase being monitored by <see cref="AgentStallMonitor"/>, plus the
/// sinks the monitor reports stall messages to.
/// </summary>
/// <param name="AgentProvider">Agent whose health is polled and which is killed on a hard stall.</param>
/// <param name="Run">Run that receives stall messages in its chat history.</param>
/// <param name="Config">Provides the stall poll/warning intervals and the kill timeout (<c>AgentTimeout</c>).</param>
/// <param name="PhaseDescription">Human-readable phase name used in stall messages.</param>
/// <param name="OnChange">Invoked after a stall message is added to the run; may be null.</param>
/// <param name="Logger">Logger for stall warnings and errors.</param>
internal sealed record AgentMonitorContext(
    IAgentProvider AgentProvider,
    PipelineRun Run,
    PipelineConfiguration Config,
    string PhaseDescription,
    Action? OnChange,
    Serilog.ILogger Logger);

/// <summary>
/// Reusable stall detection for agent interactions. Wraps an <see cref="IAgentProvider.ExecuteAsync"/>
/// call with a background monitor that polls health status, logs silence warnings with phase context,
/// detects process death, and forcefully kills unresponsive agents after a hard timeout.
/// Also creates a per-session OTel span with GenAI semantic convention attributes.
/// </summary>
internal static class AgentStallMonitor
{
    /// <summary>
    /// Executes an agent request with background stall monitoring and per-session span.
    /// </summary>
    /// <param name="monitor">The agent, run, configuration, phase description and stall sinks to monitor with.</param>
    /// <param name="request">The agent request to execute.</param>
    /// <param name="ct">Cancellation token for the agent call.</param>
    /// <param name="onOutputLine">Receives agent output lines; may be null.</param>
    /// <param name="reportStallEvent">
    /// Reports a stall to the API as <c>(phase, kind)</c> when the agent is killed for silence or its
    /// process dies; the API records <c>pipeline.run.agent_stalls</c> (issue #2979). Null skips reporting.
    /// </param>
    /// <param name="phase">
    /// Phase key (e.g. "analysis", "codegen", "review_correctness"). When non-null, a session entry is
    /// added to the monitored run's <see cref="RunMetrics.PhaseBreakdown"/> under this key.
    /// The span's <c>pipeline.phase</c> attribute and the reported stall phase use its
    /// <see cref="PipelineTelemetry.NormalizeRunPhase"/> value, or, when null, the phase derived from
    /// <see cref="AgentMonitorContext.PhaseDescription"/>. The key itself is kept on the span as <c>pipeline.phase_key</c>.
    /// </param>
    /// <param name="timeProvider">
    /// Time source used for all clock reads and delays. Defaults to <see cref="TimeProvider.System"/>.
    /// Pass a fake/controllable provider in tests to eliminate wall-clock dependency.
    /// </param>
    public static async Task<AgentResult> ExecuteWithMonitoringAsync(
        AgentMonitorContext monitor,
        AgentRequest request,
        CancellationToken ct,
        Action<string>? onOutputLine = null,
        Action<string, string>? reportStallEvent = null,
        TimeProvider? timeProvider = null,
        string? phase = null)
    {
        var agentProvider = monitor.AgentProvider;
        var run = monitor.Run;
        timeProvider ??= TimeProvider.System;
        var startTime = timeProvider.GetUtcNow();
        var providerName = GetProviderName(agentProvider);
        var phaseTag = phase is not null
            ? PipelineTelemetry.NormalizeRunPhase(phase)
            : PipelineTelemetry.NormalizePhaseDescription(monitor.PhaseDescription);
        var model = agentProvider.Model;

        // Session span: one per agent CLI invocation, following GenAI semantic conventions
        using var sessionSpan = PipelineTelemetry.ActivitySource.StartActivity(
            $"invoke_agent {phaseTag}",
            ActivityKind.Client);

        if (sessionSpan is not null)
        {
            sessionSpan.SetTag("gen_ai.operation.name", "invoke_agent");
            sessionSpan.SetTag("gen_ai.provider.name", providerName);
            sessionSpan.SetTag("pipeline.phase", phaseTag);
            if (phase is not null)
                sessionSpan.SetTag("pipeline.phase_key", phase);
            sessionSpan.SetTag("agent.session.resumed", request.UseResume || request.ResumeSessionId is not null);
            if (model is not null)
                sessionSpan.SetTag("gen_ai.request.model", model);
        }

        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitorTask = RunMonitorLoopAsync(
            new MonitorLoopState(monitor, phaseTag, reportStallEvent, timeProvider, sessionSpan), stallCts.Token);

        AgentResult result;
        try
        {
            result = await agentProvider.ExecuteAsync(request, ct, onOutputLine);
        }
        catch (Exception ex)
        {
            // TODO: Passing ct here resolves to RecordError(Exception, TagList) with the token
            // implicitly converted to an ActivityTagsCollection. If ct is already cancelled when
            // the provider throws a non-cancellation exception, the span may receive an incorrect
            // cancelled status instead of an error tag. Consider passing escaped: false (or no
            // second argument) to unconditionally record the error status.
            sessionSpan?.RecordError(ex, ct);
            throw;
        }
        finally
        {
            await stallCts.CancelAsync();
            try { await monitorTask; } catch (OperationCanceledException) { /* Expected on cancellation. */ }
        }

        if (sessionSpan is not null)
            TagSessionResult(sessionSpan, result);

        // Accumulate session timing and count into the run's phase breakdown when a phase is known
        if (phase is not null)
        {
            var elapsedSeconds = (timeProvider.GetUtcNow() - startTime).TotalSeconds;
            run.AccumulateAgentSession(phase, elapsedSeconds, providerName, model);
        }

        return result;
    }

    /// <summary>
    /// Sets the exit code, token usage, cost and provider-reported usage details on the session span.
    /// </summary>
    private static void TagSessionResult(Activity sessionSpan, AgentResult result)
    {
        sessionSpan.SetTag("agent.exit_code", result.ExitCode);
        if (result.Usage is { } usage)
        {
            sessionSpan.SetTag("gen_ai.usage.input_tokens", usage.InputTokens);
            sessionSpan.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);
            SetTagIfPositive(sessionSpan, "gen_ai.usage.total_tokens", usage.TotalTokens);
            SetTagIfPositive(sessionSpan, "gen_ai.usage.reasoning_tokens", usage.ReasoningTokens);
            SetTagIfPositive(sessionSpan, "gen_ai.usage.cache_read_input_tokens", usage.CacheReadTokens);
            SetTagIfPositive(sessionSpan, "gen_ai.usage.cache_creation_input_tokens", usage.CacheWriteTokens);
        }

        if (result.Cost is { } cost)
            sessionSpan.SetTag("agent.cost_usd", (double)cost);

        // Every provider can classify a failure; only some report usage details.
        if (result.ErrorCategory != AgentErrorCategory.None)
            sessionSpan.SetTag("agent.error_category", result.ErrorCategory.ToString());

        if (result.UsageDetails is not { } details)
            return;

        sessionSpan.SetTag("agent.billing", details.BillingMode);
        sessionSpan.SetTag("agent.turns", details.Turns);
        sessionSpan.SetTag("agent.api_duration_s", details.ApiDurationSeconds);
        if (details.WebSearchRequests > 0)
            sessionSpan.SetTag("agent.web_search_requests", details.WebSearchRequests);
    }

    private static void SetTagIfPositive(Activity span, string key, long value)
    {
        if (value > 0)
            span.SetTag(key, value);
    }

    /// <summary>
    /// Monitors an arbitrary async agent call (e.g., <see cref="IAgentProvider.EnsureSessionAsync"/>)
    /// that does not return an <see cref="AgentResult"/>.
    /// </summary>
    /// <param name="monitor">The agent, run, configuration, phase description and stall sinks to monitor with.</param>
    /// <param name="agentCall">The agent call to monitor.</param>
    /// <param name="ct">Cancellation token; cancelling it stops the monitor.</param>
    /// <param name="reportStallEvent">Reports a stall to the API as <c>(phase, kind)</c>; null skips reporting.</param>
    /// <param name="timeProvider">
    /// Time source used for all clock reads and delays. Defaults to <see cref="TimeProvider.System"/>.
    /// Pass a fake/controllable provider in tests to eliminate wall-clock dependency.
    /// </param>
    public static async Task MonitorAsync(
        AgentMonitorContext monitor,
        Func<Task> agentCall,
        CancellationToken ct,
        Action<string, string>? reportStallEvent = null,
        TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitorTask = RunMonitorLoopAsync(
            new MonitorLoopState(monitor, PipelineTelemetry.NormalizePhaseDescription(monitor.PhaseDescription),
                reportStallEvent, timeProvider, null),
            stallCts.Token);

        try
        {
            await agentCall();
        }
        finally
        {
            await stallCts.CancelAsync();
            try { await monitorTask; } catch (OperationCanceledException) { /* Expected on cancellation. */ }
        }
    }

    /// <summary>
    /// Maps an <see cref="IAgentProvider"/> to a closed-set provider name tag
    /// for <c>pipeline.run.*</c> metrics and span attributes.
    /// </summary>
    internal static string GetProviderName(IAgentProvider agentProvider) =>
        agentProvider.ProviderType switch
        {
            AgentProviderType.KiroCli => PipelineTelemetry.RunProviders.Kiro,
            AgentProviderType.OpenCode => PipelineTelemetry.RunProviders.OpenCode,
            AgentProviderType.ClaudeCode => PipelineTelemetry.RunProviders.Claude,
            _ => PipelineTelemetry.RunProviders.Unknown
        };

    /// <summary>
    /// Per-invocation state shared by the monitor loop and its stall handlers.
    /// </summary>
    private sealed record MonitorLoopState(
        AgentMonitorContext Monitor,
        string PhaseTag,
        Action<string, string>? ReportStallEvent,
        TimeProvider TimeProvider,
        Activity? SessionSpan)
    {
        /// <summary>Reports a stall of the given kind for this phase, when a reporter is wired.</summary>
        public void ReportStall(string kind) => ReportStallEvent?.Invoke(PhaseTag, kind);
    }

    private static Task RunMonitorLoopAsync(MonitorLoopState state, CancellationToken stallToken)
    {
        var monitor = state.Monitor;
        var timeProvider = state.TimeProvider;
        var killTimeout = monitor.Config.AgentTimeout;

        return Task.Run(async () =>
        {
            try
            {
                var lastWarnTime = timeProvider.GetUtcNow().UtcDateTime;

                while (!stallToken.IsCancellationRequested)
                {
                    await Task.Delay(monitor.Config.StallPollInterval, timeProvider, stallToken);

                    if (!TryGetHealth(monitor, out var health))
                        continue;

                    if (HandleProcessDeath(health!, state))
                        break;

                    var silence = ComputeSilence(health!, monitor.Run, timeProvider);

                    if (await HandleKillTimeoutAsync(silence, killTimeout, state))
                        break;

                    HandleSilenceWarning(health!, silence, state, ref lastWarnTime);
                }
            }
            catch (OperationCanceledException) { /* Expected on cancellation. */ }
            catch (ObjectDisposedException) { /* Already disposed. */ }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Calls <see cref="IAgentProvider.GetHealthStatus"/> safely.
    /// Returns false (and logs) when the call throws, allowing the monitor loop to continue.
    /// </summary>
    private static bool TryGetHealth(AgentMonitorContext monitor, out AgentHealthStatus? health)
    {
        try
        {
            health = monitor.AgentProvider.GetHealthStatus();
            return true;
        }
        catch (Exception ex)
        {
            monitor.Logger.Warning(ex, "Pipeline {RunId} GetHealthStatus() call failed, continuing to poll", monitor.Run.RunId);
            health = null;
            return false;
        }
    }

    /// <summary>
    /// Checks whether the agent process has died.
    /// Logs an error and notifies when true; returns true to break the monitor loop.
    /// </summary>
    private static bool HandleProcessDeath(AgentHealthStatus health, MonitorLoopState state)
    {
        if (health.IsProcessAlive == false)
        {
            var monitor = state.Monitor;
            var run = monitor.Run;
            var errorMsg = $"{monitor.PhaseDescription} — agent process is no longer alive (PID {health.ProcessId}). " +
                           $"Total elapsed: {(state.TimeProvider.GetUtcNow() - run.StartedAtOffset):hh\\:mm\\:ss}.";
            monitor.Logger.Error("Pipeline {RunId} {StallMessage}", run.RunId, errorMsg);
            run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = errorMsg });
            monitor.OnChange?.Invoke();
            state.ReportStall(PipelineTelemetry.AgentStallKinds.ProcessDeath);

            state.SessionSpan?.AddEvent(new ActivityEvent("agent.process_death",
                tags: new ActivityTagsCollection { { "pid", health.ProcessId } }));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Computes the silence duration using <see cref="AgentHealthStatus.LastOutputTime"/>
    /// or the run start time as a fallback.
    /// </summary>
    private static TimeSpan ComputeSilence(AgentHealthStatus health, PipelineRun run, TimeProvider timeProvider)
    {
        var referenceTime = health.LastOutputTime ?? run.StartedAtOffset.UtcDateTime;
        return timeProvider.GetUtcNow().UtcDateTime - referenceTime;
    }

    /// <summary>
    /// Handles the hard-kill case when silence exceeds the kill timeout.
    /// Logs, notifies, kills the agent, and returns true to break the monitor loop.
    /// </summary>
    private static async Task<bool> HandleKillTimeoutAsync(TimeSpan silence, TimeSpan killTimeout, MonitorLoopState state)
    {
        if (silence < killTimeout)
            return false;

        var monitor = state.Monitor;
        var run = monitor.Run;
        var killMsg = $"{monitor.PhaseDescription} — no output for {silence.TotalMinutes:F0}m (kill timeout {killTimeout.TotalMinutes:F0}m). " +
                      $"Forcefully terminating agent process.";
        monitor.Logger.Error("Pipeline {RunId} {StallMessage}", run.RunId, killMsg);
        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = killMsg });
        monitor.OnChange?.Invoke();
        state.ReportStall(PipelineTelemetry.AgentStallKinds.StallKill);

        state.SessionSpan?.AddEvent(new ActivityEvent("agent.stall_kill",
            tags: new ActivityTagsCollection
            {
                { "silence_minutes", (long)silence.TotalMinutes },
                { "kill_timeout_minutes", (long)killTimeout.TotalMinutes }
            }));

        try { await monitor.AgentProvider.KillAsync(); }
        catch (Exception ex) { monitor.Logger.Warning(ex, "Pipeline {RunId} KillAsync() failed", run.RunId); }
        return true;
    }

    /// <summary>
    /// Emits a silence warning when the silence threshold is met and sufficient time has
    /// passed since the last warning. Updates <paramref name="lastWarnTime"/> on emit.
    /// </summary>
    private static void HandleSilenceWarning(
        AgentHealthStatus health, TimeSpan silence,
        MonitorLoopState state, ref DateTime lastWarnTime)
    {
        var monitor = state.Monitor;
        var config = monitor.Config;
        var run = monitor.Run;
        var timeProvider = state.TimeProvider;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var timeSinceLastWarn = now - lastWarnTime;
        if (silence < config.StallWarningInterval || timeSinceLastWarn < config.StallWarningInterval)
            return;

        var elapsed = timeProvider.GetUtcNow() - run.StartedAtOffset;
        var statusDetail = health.SessionStatus is not null ? $" Session status: {health.SessionStatus}." : "";
        var statusMsg = health.SessionStatusMessage is not null ? $" Detail: {health.SessionStatusMessage}" : "";
        var sessionsSummary = health.AllSessionsSummary is not null ? $" Sessions: [{health.AllSessionsSummary}]" : "";
        var msg = $"{monitor.PhaseDescription} — no output for {silence.TotalMinutes:F0}m. " +
                  $"Agent call still in progress. " +
                  $"Total elapsed: {elapsed:hh\\:mm\\:ss}. Timeout: {config.AgentTimeout:hh\\:mm\\:ss}." +
                  statusDetail + statusMsg + sessionsSummary;
        monitor.Logger.Warning("Pipeline {RunId} {StallMessage}", run.RunId, msg);
        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = msg });
        monitor.OnChange?.Invoke();

        state.SessionSpan?.AddEvent(new ActivityEvent("agent.stall_warning",
            tags: new ActivityTagsCollection { { "silence_minutes", (long)silence.TotalMinutes } }));
        lastWarnTime = now;
    }
}
