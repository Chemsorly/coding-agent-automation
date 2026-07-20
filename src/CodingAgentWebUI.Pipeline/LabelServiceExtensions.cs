using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline;

/// <summary>
/// Extension methods for <see cref="ILabelService"/> providing best-effort (non-fatal) label swap operations.
/// Centralizes the try/catch + warning pattern used across multiple call sites.
/// </summary>
public static class LabelServiceExtensions
{
    /// <summary>
    /// Best-effort label swap: catches all exceptions except <see cref="OperationCanceledException"/>,
    /// logs a warning, and continues. Use for non-fatal label operations where failure should not
    /// interrupt the calling workflow.
    /// </summary>
    public static async Task TrySwapLabelAsync(
        this ILabelService labelService,
        ProviderConfigId providerConfigId,
        string identifier,
        string newLabel,
        LabelTargetKind targetKind,
        ILogger logger,
        string context,
        CancellationToken ct)
    {
        try
        {
            await labelService.SwapLabelAsync(providerConfigId, identifier, newLabel, targetKind, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex, "{Context}: label swap to {Label} failed for {Identifier} (non-fatal)",
                context, newLabel, identifier);
        }
    }

    /// <summary>
    /// Convenience overload accepting a <see cref="PipelineRun"/> — uses
    /// <see cref="PipelineRun.ProviderConfigIdForLabel"/> and <see cref="PipelineRun.LabelTargetKind"/>
    /// to ensure correct routing for both Issue and Review runs.
    /// </summary>
    public static Task TrySwapLabelAsync(
        this ILabelService labelService,
        PipelineRun run,
        string newLabel,
        ILogger logger,
        string context,
        CancellationToken ct)
    {
        return labelService.TrySwapLabelAsync(
            run.ProviderConfigIdForLabel,
            run.IssueIdentifier,
            newLabel,
            run.LabelTargetKind,
            logger, context, ct);
    }
}
