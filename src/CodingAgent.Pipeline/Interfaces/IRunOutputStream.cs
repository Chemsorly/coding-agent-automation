using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Manages per-run output streaming.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="AppendOutputLines"/> is the only way to record output.</b>
/// <see cref="GetOutputBacklogAsync"/> returns the lines oldest-first (Redis keeps the last 500),
/// or an empty list for an unknown run.
/// </para>
/// </remarks>
public interface IRunOutputStream
{
    /// <summary>
    /// Appends output lines to the run's persistent storage.
    /// For in-memory implementations this writes to the <c>OutputRingBuffer</c>.
    /// For distributed implementations (Redis) this writes to the Redis List for
    /// cross-replica backlog serving.
    /// </summary>
    void AppendOutputLines(RunId runId, IReadOnlyList<string> lines);

    /// <summary>
    /// Returns the full output backlog for a run, oldest lines first.
    /// Returns an empty list for an unknown run.
    /// For distributed implementations, reads from the Redis List (capped at 500 lines).
    /// </summary>
    Task<IReadOnlyList<string>> GetOutputBacklogAsync(RunId runId, CancellationToken ct = default);
}
