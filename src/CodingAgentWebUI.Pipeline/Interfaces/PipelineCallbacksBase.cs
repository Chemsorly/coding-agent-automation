using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Abstract base class for <see cref="IPipelineCallbacks"/> implementations.
/// Captures shared behavioral policies that both orchestrator-side and agent-side adapters duplicate:
/// <list type="bullet">
/// <item>Non-fatal error handling for <see cref="IPipelineCallbacks.CreateDraftPrIfNotExists"/>
///   (catch non-OCE, log warning, continue)</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// This base class does NOT attempt to unify the composition strategy of the subclasses.
/// <c>OrchestratorCallbacks</c> captures a service reference and closure accessor;
/// <c>AgentCallbacks</c> accepts Action/Func delegates. The base class only captures
/// the shared behavioral policies as protected helper methods.
/// </para>
/// <para>
/// The <see cref="PipelineRun.LabelTargetKind"/> property on <c>PipelineRun</c> centralizes
/// the RunType→LabelTargetKind routing policy that was previously duplicated in each adapter.
/// Subclasses should use <c>run.LabelTargetKind</c> instead of inline switch expressions.
/// </para>
/// </remarks>
public abstract class PipelineCallbacksBase : IPipelineCallbacks
{
    /// <inheritdoc />
    public abstract void TransitionTo(PipelineStep step);

    /// <inheritdoc />
    public abstract void EmitOutputLine(string line);

    /// <inheritdoc />
    public abstract void NotifyChange();

    /// <inheritdoc />
    public abstract void AddRunToHistory(PipelineRun run);

    /// <inheritdoc />
    public abstract Task UpdateFileChangeStats(PipelineRun run);

    /// <inheritdoc />
    public abstract Task SwapAgentLabel(string issueIdentifier, string label, CancellationToken ct);

    /// <inheritdoc />
    public abstract Task RemoveAllAgentLabels(string issueIdentifier, CancellationToken ct);

    /// <inheritdoc />
    public abstract Task CreatePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct);

    /// <summary>
    /// Creates a draft PR if one does not already exist, swallowing non-fatal errors.
    /// Subclasses implement <see cref="CreateDraftPrCoreAsync"/> for the actual creation logic.
    /// On success, emits an output line with the PR number.
    /// On failure (non-OCE), logs via <see cref="LogDraftPrWarning"/> and continues.
    /// </summary>
    // TODO: Add unit tests for this template method verifying: (1) success + PullRequestNumber set
    // emits output line, (2) success + PullRequestNumber empty/null does not emit, (3) non-OCE
    // exception calls LogDraftPrWarning and continues, (4) OperationCanceledException propagates.
    // (Review finding: missing characterization tests for shared error-handling pattern)
    public async Task CreateDraftPrIfNotExists(PipelineRun run, CancellationToken ct)
    {
        try
        {
            await CreateDraftPrCoreAsync(run, ct);
            // TODO: This checks run.PullRequestNumber but the original OrchestratorCallbacks checked
            // the return value (prUrl != null) from CreateDraftPrIfNotExistsAsync. These differ in the
            // early-return path where run.LinkedPullRequest != null — the method returns a non-null URL
            // without setting run.PullRequestNumber. Consider changing CreateDraftPrCoreAsync to return
            // Task<string?> and checking the return value instead. (Review finding: behavioral delta)
            if (!string.IsNullOrEmpty(run.PullRequestNumber))
                EmitOutputLine($"📋 Draft PR #{run.PullRequestNumber} created");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal: CI can still run without a PR existing
            LogDraftPrWarning(run, ex);
        }
    }

    /// <summary>
    /// Core draft PR creation logic. Called within the error-handling wrapper of
    /// <see cref="CreateDraftPrIfNotExists"/>. Implementations should perform the actual
    /// PR creation and populate <see cref="PipelineRun.PullRequestNumber"/> on success.
    /// </summary>
    protected abstract Task CreateDraftPrCoreAsync(PipelineRun run, CancellationToken ct);

    /// <summary>
    /// Logs a warning when draft PR creation fails. Subclasses provide the appropriate
    /// logging mechanism (Serilog static logger, injected ILogger, etc.).
    /// </summary>
    protected abstract void LogDraftPrWarning(PipelineRun run, Exception ex);

    /// <inheritdoc />
    public abstract Task FinalizePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct);

    /// <inheritdoc />
    public abstract Task ReportBrainSyncResult(bool contextLoaded, int knowledgeFileCount);
}
