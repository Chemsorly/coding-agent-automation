using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Downloads open issues and writes them as markdown context files to the workspace
/// for agent deduplication. Uses <see cref="IOpenIssueContextWriter"/> (which proxies
/// through <see cref="IAgentIssueOperations"/> via SignalR) — not IIssueProvider directly.
/// For decomposition runs, also includes recently-closed sibling issues.
/// </summary>
public sealed class WriteOpenIssueContextStep : IPipelineStep
{
    public string StepName => "WriteOpenIssueContext";

    private readonly IOpenIssueContextWriter _writer;

    public WriteOpenIssueContextStep(IOpenIssueContextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        context.Callbacks.TransitionTo(PipelineStep.DownloadingOpenIssues);

        var maxIssues = context.Config.MaxOpenIssuesForContext;
        var includeClosedSiblings = IsEpicScopedRun(context.Run.RunType);

        // Uses IAgentIssueOperations (proxied through SignalR) — not IIssueProvider directly
        var count = await _writer.WriteOpenIssueContextAsync(
            context.IssueOps, context.Run.WorkspacePath!, maxIssues, includeClosedSiblings, ct);

        context.Run.OpenIssuesDownloaded = count;
        context.Logger.Information("Wrote {Count} issue context files (includeClosedSiblings={IncludeClosed})",
            count, includeClosedSiblings);
        return StepResult.Continue;
    }

    /// <summary>
    /// Determines whether the run is epic-scoped (decomposition phase 1 or 2).
    /// Epic-scoped runs benefit from seeing recently-closed sibling issues.
    /// </summary>
    internal static bool IsEpicScopedRun(PipelineRunType runType) =>
        runType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition;
}
