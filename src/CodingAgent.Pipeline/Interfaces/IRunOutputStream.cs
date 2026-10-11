using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Manages per-run output streaming, chat history, and quality-gate history.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="AppendOutputLines"/> is the only way to record output.</b>
/// <see cref="GetOutputBacklogAsync"/> returns the lines oldest-first (Redis keeps the last 500),
/// or an empty list for an unknown run.
/// </para>
/// <para>
/// <b><see cref="AppendChatEntry"/> is the only way to record chat history.</b>
/// Changing <c>ChatHistory</c> on a <c>GetRun</c> result is not stored by the Redis implementation.
/// <see cref="GetChatHistoryAsync"/> returns entries oldest-first (Redis keeps the last 200),
/// or an empty list for an unknown run.
/// </para>
/// <para>
/// <b><see cref="AppendQualityGateReport"/> is the only way to record quality-gate history.</b>
/// Changing <c>QualityGateHistory</c> on a <c>GetRun</c> result is not stored by the Redis implementation.
/// <see cref="GetQualityGateHistoryAsync"/> returns reports oldest-first (Redis keeps the last
/// <see cref="PipelineConstants.DefaultQualityGateHistoryCapacity"/> entries),
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

    /// <summary>
    /// Appends one chat entry to the run's chat history.
    /// In-memory: enqueues on the active run's <c>ChatHistory</c> (no-op for an unknown run);
    /// distributed (Redis): pushes to <c>run:{id}:chat</c>, capped at <see cref="PipelineConstants.DefaultChatHistoryCapacity"/> entries.
    /// <b>This is the only way to record chat history; changing <c>ChatHistory</c> on a <c>GetRun</c> result is not stored by the Redis implementation.</b>
    /// </summary>
    void AppendChatEntry(RunId runId, ChatEntry entry);

    /// <summary>
    /// Returns the run's chat history, oldest entries first.
    /// Returns an empty list for an unknown run.
    /// For distributed implementations, reads from the Redis List (capped at 200 entries).
    /// </summary>
    Task<IReadOnlyList<ChatEntry>> GetChatHistoryAsync(RunId runId, CancellationToken ct = default);

    /// <summary>
    /// Appends one quality-gate report to the run's persistent history.
    /// In-memory: enqueues on the active run's <c>QualityGateHistory</c> (no-op for an unknown run);
    /// distributed (Redis): pushes to <c>run:{id}:qg</c>, capped at <see cref="PipelineConstants.DefaultQualityGateHistoryCapacity"/> entries.
    /// <b>This is the only way to record quality-gate history; changing <c>QualityGateHistory</c> on a <c>GetRun</c> result is not stored by the Redis implementation.</b>
    /// </summary>
    void AppendQualityGateReport(RunId runId, QualityGateReport report);

    /// <summary>
    /// Returns the run's quality-gate history, oldest entries first.
    /// Returns an empty list for an unknown run.
    /// For distributed implementations, reads from the Redis List (capped at
    /// <see cref="PipelineConstants.DefaultQualityGateHistoryCapacity"/> entries).
    /// </summary>
    Task<IReadOnlyList<QualityGateReport>> GetQualityGateHistoryAsync(RunId runId, CancellationToken ct = default);
}
