using CodingAgentWebUI.Infrastructure;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Factory methods for creating <see cref="OutputBatcher"/> instances pre-wired with
/// standard hub flush error handling (try/catch/log-warning pattern).
/// </summary>
public static class OutputBatcherHubExtensions
{
    /// <summary>
    /// Creates an <see cref="OutputBatcher"/> wired with standard hub flush error handling.
    /// The <paramref name="flushAction"/> is invoked with each batch of lines; exceptions are
    /// caught and logged as warnings using the provided <paramref name="logger"/>.
    /// </summary>
    /// <param name="flushAction">
    /// The async action to invoke on each flush (e.g., a SignalR hub invocation).
    /// Receives the batch of lines collected since the last flush.
    /// </param>
    /// <param name="logger">Logger used to emit a warning when the flush action throws.</param>
    /// <param name="failureMessage">
    /// The message template logged on failure. Defaults to <c>"Failed to send output lines batch"</c>.
    /// </param>
    /// <returns>
    /// A new <see cref="OutputBatcher"/> instance with <see cref="OutputBatcher.OnFlush"/> wired.
    /// Callers should dispose via <c>await using</c> to flush remaining buffered lines.
    /// </returns>
    // TODO: Add ArgumentNullException.ThrowIfNull for flushAction and logger to fail fast with a clear message,
    // consistent with the codebase's validation pattern (130+ occurrences of ThrowIfNull in the Agent project).
    public static OutputBatcher CreateWithHubFlush(
        Func<IReadOnlyList<string>, Task> flushAction,
        Serilog.ILogger logger,
        string failureMessage = "Failed to send output lines batch")
    {
        var batcher = new OutputBatcher();
        batcher.OnFlush += async lines =>
        {
            try
            {
                await flushAction(lines);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, failureMessage);
            }
        };
        return batcher;
    }
}
