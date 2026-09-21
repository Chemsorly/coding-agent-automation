using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Serilog;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Downloads open issues and writes them as markdown context files to the workspace
/// for agent deduplication. Delegates all writing to <see cref="IOpenIssueContextWriter"/>.
/// For decomposition runs, also includes recently-closed sibling issues.
/// </summary>
/// <remarks>
/// <para>
/// Can be constructed in two ways:
/// <list type="bullet">
/// <item><description>No-arg constructor — creates a default <see cref="OpenIssueContextWriter"/> using the step context's logger (default for agent pipelines).</description></item>
/// <item><description>Constructor accepting <see cref="IOpenIssueContextWriter"/> — delegates all writing
/// to the provided writer; useful for unit testing via mock injection.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class WriteOpenIssueContextStep : IPipelineStep
{
    private readonly IOpenIssueContextWriter? _writer;

    /// <summary>
    /// Initializes a <see cref="WriteOpenIssueContextStep"/> that creates a default
    /// <see cref="OpenIssueContextWriter"/> at execution time using the step context's logger.
    /// This is the production path used by the agent pipeline builder.
    /// </summary>
    public WriteOpenIssueContextStep()
    {
        _writer = null;
    }

    /// <summary>
    /// Initializes a <see cref="WriteOpenIssueContextStep"/> that delegates all issue writing
    /// to the supplied <paramref name="writer"/>. This overload exists to support unit testing
    /// without hitting the file system.
    /// </summary>
    /// <param name="writer">The <see cref="IOpenIssueContextWriter"/> to delegate to.</param>
    public WriteOpenIssueContextStep(IOpenIssueContextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    public string StepName => "WriteOpenIssueContext";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        context.Callbacks.TransitionTo(PipelineStep.DownloadingOpenIssues);

        var maxIssues = context.Config.MaxOpenIssuesForContext;
        var includeClosedSiblings = IsEpicScopedRun(context.Run.RunType);

        // Use injected writer (tests) or create a default one for production
        var writer = _writer ?? new OpenIssueContextWriter(context.Logger);

        var count = await writer.WriteOpenIssueContextAsync(
            context.IssueOps,
            context.Run.WorkspacePath!,
            maxIssues,
            includeClosedSiblings,
            ct);

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
