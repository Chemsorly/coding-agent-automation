using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Abstract base class for <see cref="IPipelineCallbacks"/> implementations that captures shared
/// behavioral policy: the <see cref="PipelineRun.RunType"/>→<see cref="LabelTargetKind"/> routing
/// and the non-fatal <see cref="CreateDraftPrIfNotExists"/> error-swallowing pattern.
/// </summary>
/// <remarks>
/// <para>
/// Concrete subclasses (<c>OrchestratorCallbacks</c> in <c>PipelineOrchestrationService</c>,
/// <c>AgentCallbacks</c> in <c>LocalPipelineExecutor</c>) retain their transport-specific
/// delegation strategies (closure-based vs delegate-based). This base class only extracts the
/// shared policy logic that previously had to be updated in lockstep across both adapters.
/// </para>
/// <para>
/// The <see cref="CreateDraftPrIfNotExists"/> template method delegates the actual PR creation
/// to <see cref="CreateDraftPrCoreAsync"/> and wraps it with a non-fatal try/catch. Subclasses
/// override the core method and optionally override <see cref="LogDraftPrFailure"/> for
/// host-specific logging.
/// </para>
/// </remarks>
public abstract class PipelineCallbacksBase : IPipelineCallbacks
{
    /// <summary>Gets the <see cref="PipelineRun"/> associated with this callbacks instance.</summary>
    protected abstract PipelineRun Run { get; }

    /// <summary>
    /// Returns the <see cref="LabelTargetKind"/> for the current run.
    /// Review runs target pull requests; all other run types target issues.
    /// </summary>
    protected LabelTargetKind GetLabelTargetKind() => Run.LabelTargetKind;

    /// <inheritdoc />
    public abstract void TransitionTo(PipelineStep step);

    /// <inheritdoc />
    public abstract void EmitOutputLine(string line);

    /// <inheritdoc />
    public abstract void NotifyChange();

    /// <inheritdoc />
    public abstract Task AddRunToHistoryAsync(PipelineRun run);

    /// <inheritdoc />
    public abstract Task UpdateFileChangeStats(PipelineRun run);

    /// <inheritdoc />
    public abstract Task SwapAgentLabel(string issueIdentifier, string label, CancellationToken ct);

    /// <inheritdoc />
    public abstract Task RemoveAllAgentLabels(string issueIdentifier, CancellationToken ct);

    /// <inheritdoc />
    public abstract Task CreatePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct);

    /// <summary>
    /// Creates a draft pull request if one does not already exist for this run.
    /// Wraps <see cref="CreateDraftPrCoreAsync"/> with non-fatal error handling:
    /// failures are logged and swallowed (CI can still run without a PR existing).
    /// On success, emits an output line indicating the draft PR was created.
    /// </summary>
    /// <remarks>
    /// Override this method entirely when the host already encapsulates the error-swallowing
    /// pattern in a separate method (e.g., <c>PipelineOrchestrationService.CreateDraftPrIfNotExistsAsync</c>).
    /// </remarks>
    // TODO: Add unit tests for this template method verifying: (1) successful creation emits output line,
    // (2) non-OperationCanceledException exceptions are swallowed and routed to LogDraftPrFailure,
    // (3) OperationCanceledException propagates unhandled.
    public virtual async Task CreateDraftPrIfNotExists(PipelineRun run, CancellationToken ct)
    {
        try
        {
            await CreateDraftPrCoreAsync(run, ct);
            if (!string.IsNullOrEmpty(run.PullRequestNumber))
                EmitOutputLine($"📋 Draft PR #{run.PullRequestNumber} created");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDraftPrFailure(run, ex);
        }
    }

    /// <summary>
    /// Performs the actual draft PR creation. Called by <see cref="CreateDraftPrIfNotExists"/>.
    /// Implementations should create the draft PR and set <see cref="PipelineRun.PullRequestNumber"/>
    /// on success.
    /// </summary>
    /// <remarks>
    /// Not required when <see cref="CreateDraftPrIfNotExists"/> is overridden entirely.
    /// </remarks>
    protected virtual Task CreateDraftPrCoreAsync(PipelineRun run, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Logs a warning when draft PR creation fails. Override for host-specific logging.
    /// Default implementation uses Serilog's static <see cref="Log"/> class.
    /// </summary>
    protected virtual void LogDraftPrFailure(PipelineRun run, Exception ex)
    {
        Log.Warning(ex, "Pipeline {RunId} failed to create draft PR, continuing", run.RunId);
    }

    /// <inheritdoc />
    public abstract Task FinalizePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct);

    /// <inheritdoc />
    public abstract Task ReportBrainSyncResult(bool contextLoaded, int knowledgeFileCount);
}
