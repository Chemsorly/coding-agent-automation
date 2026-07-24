using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Encapsulates all SignalR communication concerns for pipeline execution.
/// Owns the <see cref="SemaphoreSlim"/> used to serialize fire-and-forget sends,
/// the <c>*InternalAsync</c> methods that perform the actual hub calls, and
/// static helpers (<see cref="BuildStepMetadata"/>, <see cref="MaskSecretsInOutput"/>).
/// </summary>
/// <remarks>
/// <para>Extracted from <see cref="LocalPipelineExecutor"/> to separate communication
/// from orchestration. The reporter is instantiated per-job in
/// <c>ExecutePipelineStepsAsync</c> and disposed via <see cref="IAsyncDisposable"/>
/// which drains in-flight sends before releasing the semaphore.</para>
/// <para>Two distinct step transition paths exist:</para>
/// <list type="bullet">
///   <item><see cref="TransitionToInternalAsync"/> — fire-and-forget via <c>SendAsync</c>,
///     serialized through the semaphore, includes metadata from <see cref="BuildStepMetadata"/>.</item>
///   <item><see cref="ReportStepTransitionAsync"/> — awaited via <c>InvokeAsync</c>,
///     no semaphore, no metadata. Used during PR creation only.</item>
/// </list>
/// </remarks>
public sealed class PipelineSignalRReporter : IAsyncDisposable
{
    private readonly SemaphoreSlim _signalrLock = new(1, 1);
    private readonly HubConnection _connection;
    private readonly OutputBatcher _outputBatcher;
    private readonly string _jobId;
    private readonly PipelineRun _run;
    private readonly Action<PipelineStep?>? _onStepChanged;
    private readonly Serilog.ILogger _logger;
    private int _disposed;

    public PipelineSignalRReporter(
        HubConnection connection,
        OutputBatcher outputBatcher,
        string jobId,
        PipelineRun run,
        Action<PipelineStep?>? onStepChanged,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(outputBatcher);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _outputBatcher = outputBatcher;
        _jobId = jobId;
        _run = run;
        _onStepChanged = onStepChanged;
        _logger = logger;
    }

    // ── Fire-and-forget delegate methods (serialized via semaphore) ──────

    /// <summary>
    /// Fire-and-forget step transition. Serialized via the internal semaphore.
    /// </summary>
    public void TransitionTo(PipelineStep step, CancellationToken ct)
        => _ = SerializedSendAsync(_signalrLock, () => TransitionToInternalAsync(step, ct), ct);

    /// <summary>
    /// Fire-and-forget output line emission with secret masking.
    /// </summary>
    public void EmitOutputLine(string line, PipelineStepContext? context, CancellationToken ct)
    {
        var masked = MaskSecretsInOutput(line, context);
        _ = EmitOutputLineInternalAsync(masked, ct);
    }

    /// <summary>
    /// Fire-and-forget quality gate result reporting. Serialized via the internal semaphore.
    /// </summary>
    public void ReportQualityGateResult(QualityGateReport report, CancellationToken ct)
        => _ = SerializedSendAsync(_signalrLock, () => ReportQualityGateResultInternalAsync(report, ct), ct);

    // ── Awaited SignalR calls (no semaphore, uses InvokeAsync) ───────────

    /// <summary>
    /// Reports brain sync result to the orchestrator. Awaited (not fire-and-forget).
    /// </summary>
    public async Task ReportBrainSyncResultAsync(bool contextLoaded, int fileCount, CancellationToken ct)
    {
        try { await _connection.InvokeAsync(HubMethodNames.ReportBrainSyncResult, _jobId, contextLoaded, fileCount, ct); }
        catch (Exception ex) { _logger.Warning(ex, "Failed to report brain sync result"); }
    }

    /// <summary>
    /// Reports a step transition to the orchestrator via SignalR, updating the run's current step.
    /// Uses <c>InvokeAsync</c> (awaited) and does NOT use the semaphore or metadata.
    /// Used during PR creation only.
    /// Failures are logged as warnings and do not propagate — step transitions are best-effort.
    /// </summary>
    internal async Task ReportStepTransitionAsync(PipelineStep step, CancellationToken ct)
    {
        _run.CurrentStep = step;
        try
        {
            await _connection.InvokeAsync(HubMethodNames.ReportStepTransition, _jobId, step, DateTimeOffset.UtcNow, (Dictionary<string, string>?)null, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to report step transition to {Step}", step);
        }
    }

    // ── Internal async implementation methods ────────────────────────────

    /// <summary>
    /// Reports a pipeline step transition with metadata. Updates run state, notifies the callback,
    /// and sends the transition to the orchestrator via SignalR. Failures are logged as warnings.
    /// </summary>
    internal async Task TransitionToInternalAsync(PipelineStep step, CancellationToken ct)
    {
        try
        {
            _run.CurrentStep = step;
            if (step is not (PipelineStep.Failed or PipelineStep.Cancelled)
                && (int)step > (int)_run.HighWaterMark)
                _run.HighWaterMark = step;

            _onStepChanged?.Invoke(step);

            var metadata = BuildStepMetadata(_run, step);
            await _connection.SendAsync(HubMethodNames.ReportStepTransition, _jobId, step, DateTimeOffset.UtcNow, metadata, ct);
        }
        catch (Exception ex)
        {
            PipelineTelemetry.AgentSignalRFailures.Add(1);
            _logger.Warning(ex, "Failed to report step transition to {Step}", step);
        }
    }

    /// <summary>
    /// Enqueues an output line to the run and batches it for delivery.
    /// Failures are logged as warnings.
    /// </summary>
    internal async Task EmitOutputLineInternalAsync(string line, CancellationToken ct)
    {
        try
        {
            _run.OutputLines.Enqueue(line);
            await _outputBatcher.AddLineAsync(line, ct);
        }
        catch (Exception ex) { _logger.Warning(ex, "Failed to batch output line"); }
    }

    /// <summary>
    /// Reports a quality gate result to the orchestrator via SignalR.
    /// Failures are logged as warnings.
    /// </summary>
    internal async Task ReportQualityGateResultInternalAsync(QualityGateReport report, CancellationToken ct)
    {
        try { await _connection.SendAsync(HubMethodNames.ReportQualityGateResult, _jobId, report, ct); }
        catch (Exception ex)
        {
            PipelineTelemetry.AgentSignalRFailures.Add(1);
            _logger.Warning(ex, "Failed to report quality gate result");
        }
    }

    // ── Static helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Serializes a fire-and-forget SignalR send behind a semaphore to guarantee ordering.
    /// Catches <see cref="OperationCanceledException"/> and <see cref="ObjectDisposedException"/>
    /// from the semaphore wait since callers discard the task — these are expected during shutdown.
    /// </summary>
    internal static async Task SerializedSendAsync(SemaphoreSlim signalrLock, Func<Task> send, CancellationToken ct)
    {
        try
        {
            await signalrLock.WaitAsync(ct);
        }
        catch (ObjectDisposedException) { return; }
        catch (OperationCanceledException) { return; }

        try { await send(); }
        finally
        {
            try { signalrLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Masks known secret values in pipeline output. If no secrets are populated on the context
    /// (i.e., before <see cref="RunEnvironmentSetupStep"/> runs), the output passes through unchanged.
    /// Values shorter than 4 characters are not masked to avoid excessive false-positive redaction.
    /// </summary>
    internal static string MaskSecretsInOutput(string output, PipelineStepContext? context)
    {
        if (context?.InjectedSecrets is not { Count: > 0 })
            return output;

        foreach (var (_, value) in context.InjectedSecrets)
        {
            if (value.Length >= 4)
                output = output.Replace(value, "***");
        }
        return output;
    }

    /// <summary>
    /// Builds metadata dictionary from the current run state to send with step transitions.
    /// Includes data from the just-completed step so the UI can display it in real-time.
    /// </summary>
    internal static Dictionary<string, string>? BuildStepMetadata(PipelineRun run, PipelineStep newStep)
    {
        // When transitioning TO a new step, the previous step just completed.
        // Include data that the previous step produced.
        Dictionary<string, string>? metadata = null;

        void Add(string key, string? value)
        {
            if (value is null) return;
            metadata ??= new Dictionary<string, string>();
            metadata[key] = value;
        }

        // CreatingBranch completed → include branch name
        if (newStep > PipelineStep.CreatingBranch && !string.IsNullOrEmpty(run.BranchName))
            Add("BranchName", run.BranchName);

        // VerifyingBaseline completed → include result
        if (newStep > PipelineStep.VerifyingBaseline && run.BaselineHealthPassed.HasValue)
            Add("BaselineHealthPassed", run.BaselineHealthPassed.Value.ToString());

        // AnalyzingCode completed → include skip status
        if (newStep > PipelineStep.AnalyzingCode && run.AnalysisSkipped)
            Add("AnalysisSkipped", "true");

        // GeneratingCode completed → include file change stats
        if (newStep > PipelineStep.GeneratingCode && run.FilesChangedCount > 0)
        {
            Add("FilesChangedCount", run.FilesChangedCount.ToString());
            Add("LinesAdded", run.LinesAdded.ToString());
            Add("LinesRemoved", run.LinesRemoved.ToString());
        }

        // ReviewingCode progress/completion
        if (newStep >= PipelineStep.ReviewingCode)
        {
            if (run.CodeReviewIterationsTotal > 0)
                Add("CodeReviewIterationsTotal", run.CodeReviewIterationsTotal.ToString());
            if (run.CodeReviewIterationsCompleted > 0)
                Add("CodeReviewIterationsCompleted", run.CodeReviewIterationsCompleted.ToString());
            if (run.CodeReviewIterationInProgress > 0)
                Add("CodeReviewIterationInProgress", run.CodeReviewIterationInProgress.ToString());
        }

        // Decomposition: open issues downloaded
        if (newStep > PipelineStep.DownloadingOpenIssues && run.OpenIssuesDownloaded > 0)
            Add("OpenIssuesDownloaded", run.OpenIssuesDownloaded.ToString());

        // Decomposition: sub-issue creation results
        if (newStep > PipelineStep.CreatingIssues && run.DecompositionSubIssuesAttempted > 0)
        {
            Add("DecompositionSubIssuesCreated", run.DecompositionSubIssuesCreated.ToString());
            Add("DecompositionSubIssuesAttempted", run.DecompositionSubIssuesAttempted.ToString());
        }

        // Quality gate retry count — report whenever retries have occurred
        if (run.RetryCount > 0)
            Add("RetryCount", run.RetryCount.ToString());
        if (run.InfrastructureRetryCount > 0)
            Add("InfrastructureRetryCount", run.InfrastructureRetryCount.ToString());

        // Token/cost accumulation — allows UI to show running totals
        if (run.TotalTokens > 0)
            Add("TotalTokens", run.TotalTokens.ToString());
        if (run.TotalCost is > 0m)
            Add("TotalCost", run.TotalCost.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Code review findings — populated during ReviewingCode step
        if (run.CodeReviewCriticalCount > 0)
            Add("CodeReviewCriticalCount", run.CodeReviewCriticalCount.ToString());
        if (run.CodeReviewWarningCount > 0)
            Add("CodeReviewWarningCount", run.CodeReviewWarningCount.ToString());
        if (run.CodeReviewSuggestionCount > 0)
            Add("CodeReviewSuggestionCount", run.CodeReviewSuggestionCount.ToString());
        if (run.CodeReviewAgentsRun.Count > 0)
            Add("CodeReviewAgentsRun", string.Join("\x1F", run.CodeReviewAgentsRun));

        return metadata;
    }

    // ── IAsyncDisposable ────────────────────────────────────────────────

    /// <summary>
    /// Drains in-flight serialized sends before disposing the semaphore. Idempotent —
    /// second and subsequent calls return immediately without side effects.
    /// <see cref="SerializedSendAsync"/> catches <see cref="ObjectDisposedException"/>,
    /// so tasks arriving after disposal are handled gracefully without unobserved exceptions.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { await _signalrLock.WaitAsync(CancellationToken.None); _signalrLock.Release(); }
        catch { /* best-effort drain */ }
        _signalrLock.Dispose();
    }
}
