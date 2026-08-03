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
        LabelSwapContext ctx)
    {
        try
        {
            await labelService.SwapLabelAsync(ctx.ProviderConfigId, ctx.Identifier, ctx.NewLabel, ctx.TargetKind, ctx.Ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.Warning(ex, "{Context}: label swap to {Label} failed for {Identifier} (non-fatal)",
                ctx.Context, ctx.NewLabel, ctx.Identifier);
        }
    }

    /// <summary>
    /// Best-effort label swap overload accepting individual parameters (backward-compatible).
    /// </summary>
    public static Task TrySwapLabelAsync(
        this ILabelService labelService,
        ProviderConfigId providerConfigId,
        IssueIdentifier identifier,
        string newLabel,
        LabelTargetKind targetKind,
        ILogger logger,
        string context,
        CancellationToken ct)
        => labelService.TrySwapLabelAsync(new LabelSwapContext(providerConfigId, identifier, newLabel, targetKind, logger, context, ct));

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
        return labelService.TrySwapLabelAsync(new LabelSwapContext(
            run.ProviderConfigIdForLabel,
            run.IssueIdentifier,
            newLabel,
            run.LabelTargetKind,
            logger, context, ct));
    }
}

/// <summary>
/// Groups the parameters for <see cref="LabelServiceExtensions.TrySwapLabelAsync"/>
/// to reduce method parameter count (S107).
/// </summary>
public sealed record LabelSwapContext(
    ProviderConfigId ProviderConfigId,
    IssueIdentifier Identifier,
    string NewLabel,
    LabelTargetKind TargetKind,
    ILogger Logger,
    string Context,
    CancellationToken Ct);
