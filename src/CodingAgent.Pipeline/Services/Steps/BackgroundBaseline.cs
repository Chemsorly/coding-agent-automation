using System.Diagnostics;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// The workspace baseline while it runs next to the analysis: <see cref="VerifyBaselineStep"/> starts it,
/// <see cref="AwaitBaselineStep"/> waits for it before code is written, and <see cref="PipelineStepRunner"/>
/// stops it when the run ends earlier, so that no build or test process outlives the steps and the workspace.
/// </summary>
internal sealed class BackgroundBaseline
{
    private readonly CancellationTokenSource _cts;
    private readonly Task<bool?> _verification;

    private BackgroundBaseline(CancellationTokenSource cts, Task<bool?> verification)
    {
        _cts = cts;
        _verification = verification;
    }

    /// <summary>Starts <paramref name="verify"/>. The returned baseline owns <paramref name="span"/> and ends it with the verification.</summary>
    internal static BackgroundBaseline Start(Func<CancellationToken, Task<bool?>> verify, Activity? span, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var verification = Task.Run(async () =>
        {
            using (span)
                return await verify(cts.Token);
        }, CancellationToken.None);
        return new BackgroundBaseline(cts, verification);
    }

    internal bool IsCompleted => _verification.IsCompleted;

    /// <summary>Waits for the verification and returns whether the baseline is healthy.</summary>
    internal Task<bool?> WaitAsync(CancellationToken ct) => _verification.WaitAsync(ct);

    /// <summary>Cancels the verification if it still runs and waits until it has ended.</summary>
    internal async Task StopAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _verification;
        }
        catch (OperationCanceledException)
        {
            // The verification ended because of the cancellation above, or because the run was cancelled.
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
