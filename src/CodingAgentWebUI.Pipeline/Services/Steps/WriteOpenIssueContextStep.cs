using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Downloads open issues and writes them as markdown context files to the workspace
/// for agent deduplication. Uses <see cref="IOpenIssueContextWriter"/> (which proxies
/// through <see cref="IAgentIssueOperations"/> via SignalR) — not IIssueProvider directly.
/// </summary>
internal sealed class WriteOpenIssueContextStep : IPipelineStep
{
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

        // Uses IAgentIssueOperations (proxied through SignalR) — not IIssueProvider directly
        var count = await _writer.WriteOpenIssueContextAsync(
            context.IssueOps, context.Run.WorkspacePath!, maxIssues, ct);

        context.Run.OpenIssuesDownloaded = count;
        context.Logger.Information("Wrote {Count} open issue context files", count);
        return StepResult.Continue;
    }
}
