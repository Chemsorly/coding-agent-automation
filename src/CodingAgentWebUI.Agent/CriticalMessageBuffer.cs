using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Bounded in-memory buffer for critical SignalR messages that failed delivery after
/// Polly retry exhaustion. Messages are replayed after successful reconnection.
/// </summary>
/// <remarks>
/// Thread-safe: <see cref="Enqueue"/> may be called from the job execution task while
/// <see cref="DrainAll"/> is called from a reconnection handler on a different thread.
/// Uses <see cref="ConcurrentQueue{T}"/> with <see cref="Interlocked"/> count tracking.
/// </remarks>
public sealed class CriticalMessageBuffer
{
    private readonly Queue<BufferedCriticalMessage> _queue = new();
    private readonly object _lock = new();

    /// <summary>Maximum number of buffered messages. Oldest are dropped on overflow.</summary>
    public const int MaxCapacity = 10;

    /// <summary>Whether the buffer contains messages awaiting replay.</summary>
    public bool HasPendingMessages
    {
        get { lock (_lock) return _queue.Count > 0; }
    }

    /// <summary>Current number of buffered messages.</summary>
    public int Count
    {
        get { lock (_lock) return _queue.Count; }
    }

    /// <summary>
    /// Enqueues a critical message for later replay. If the buffer is at capacity,
    /// the oldest message is dropped first (oldest-dropped-first overflow policy).
    /// </summary>
    public void Enqueue(BufferedCriticalMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_lock)
        {
            _queue.Enqueue(message);

            // Overflow: drop oldest messages until within capacity
            while (_queue.Count > MaxCapacity)
                _queue.Dequeue();
        }
    }

    /// <summary>
    /// Atomically drains all buffered messages and returns them in FIFO order.
    /// After this call, <see cref="HasPendingMessages"/> is false (until new enqueues).
    /// </summary>
    public IReadOnlyList<BufferedCriticalMessage> DrainAll()
    {
        lock (_lock)
        {
            var messages = _queue.ToList();
            _queue.Clear();
            return messages;
        }
    }
}

/// <summary>
/// Base type for critical messages that must survive reconnection and be replayed.
/// </summary>
/// <param name="EnqueuedAt">Timestamp when the message was first buffered.</param>
/// <param name="DrainAttempts">Number of times replay has been attempted and failed.</param>
public abstract record BufferedCriticalMessage(DateTimeOffset EnqueuedAt, int DrainAttempts = 0);

/// <summary>
/// Buffered <c>ReportJobCompleted</c> message for replay after reconnection.
/// </summary>
public sealed record BufferedJobCompleted(
    string JobId,
    JobCompletionPayload Payload,
    DateTimeOffset EnqueuedAt,
    int DrainAttempts = 0) : BufferedCriticalMessage(EnqueuedAt, DrainAttempts);
