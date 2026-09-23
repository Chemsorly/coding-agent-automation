using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Serilog;

namespace CodingAgent.Pipeline;

/// <summary>
/// Extension methods for <see cref="ILabelService"/> providing best-effort (non-fatal) label swap operations.
/// Centralizes the try/catch + warning pattern used across multiple call sites.
/// </summary>
public static class LabelServiceExtensions
{
    /// <summary>
    /// Best-effort label swap: catches all exceptions except <see cref="OperationCanceledException"/>,
    /// logs a warning, and continues. Returns <c>true</c> if the swap completed without exception,
    /// <c>false</c> if an exception was caught and swallowed.
    /// <para>
    /// When <see cref="LabelSwapContext.SwallowCancellation"/> is <c>true</c>, catches
    /// <see cref="OperationCanceledException"/> as well — use when the WorkItem is already committed
    /// to the database and the label swap is a best-effort cosmetic correction.
    /// </para>
    /// </summary>
    public static async Task<bool> TrySwapLabelAsync(
        this ILabelService labelService,
        LabelSwapContext ctx)
    {
        try
        {
            await labelService.SwapLabelAsync(ctx.ProviderConfigId, ctx.Identifier, ctx.NewLabel, ctx.TargetKind, ctx.Ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ctx.SwallowCancellation)
        {
            ctx.Logger.Warning(ex, "{Context}: label swap to {Label} failed for {Identifier} (non-fatal)",
                ctx.Context, ctx.NewLabel, ctx.Identifier);
            return false;
        }
    }

    /// <summary>
    /// Best-effort label swap overload accepting individual parameters (backward-compatible).
    /// Returns <c>true</c> if the swap applied, <c>false</c> if a non-fatal exception was swallowed.
    /// </summary>
    public static Task<bool> TrySwapLabelAsync( // NOSONAR S107 — convenience overload; delegates to LabelSwapContext
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
    /// Returns <c>true</c> if the swap applied, <c>false</c> if a non-fatal exception was swallowed.
    /// </summary>
    public static Task<bool> TrySwapLabelAsync(
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
    CancellationToken Ct)
{
    /// <summary>
    /// When <c>true</c>, <see cref="OperationCanceledException"/> is also swallowed (logged as Warning).
    /// Use on paths where the WorkItem is already committed to the database and the label swap is a
    /// best-effort cosmetic correction — there is nothing safe to revert on cancellation.
    /// Defaults to <c>false</c> (OCE propagates, preserving normal cancellation semantics).
    /// </summary>
    public bool SwallowCancellation { get; init; } = false;
}
