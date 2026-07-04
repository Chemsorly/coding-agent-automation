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
    private readonly ConcurrentQueue<BufferedCriticalMessage> _queue = new();
    private int _count;

    /// <summary>Maximum number of buffered messages. Oldest are dropped on overflow.</summary>
    public const int MaxCapacity = 10;

    /// <summary>Whether the buffer contains messages awaiting replay.</summary>
    public bool HasPendingMessages => Volatile.Read(ref _count) > 0;

    /// <summary>Current number of buffered messages.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Enqueues a critical message for later replay. If the buffer is at capacity,
    /// the oldest message is dropped first (oldest-dropped-first overflow policy).
    /// </summary>
    // TODO: Race condition in _count tracking between Enqueue and DrainAll. If DrainAll
    // dequeues a newly-enqueued item before Interlocked.Increment executes, _count can
    // transiently go negative. HasPendingMessages (checks > 0) still returns correct results
    // and the final state converges. Consider defensive clamping in Count property.
    public void Enqueue(BufferedCriticalMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        _queue.Enqueue(message);
        var newCount = Interlocked.Increment(ref _count);

        // Overflow: drop oldest messages until within capacity
        while (newCount > MaxCapacity)
        {
            if (_queue.TryDequeue(out _))
            {
                newCount = Interlocked.Decrement(ref _count);
            }
            else
            {
                break; // Concurrent drain emptied the queue
            }
        }
    }

    /// <summary>
    /// Atomically drains all buffered messages and returns them in FIFO order.
    /// After this call, <see cref="HasPendingMessages"/> is false (until new enqueues).
    /// </summary>
    public IReadOnlyList<BufferedCriticalMessage> DrainAll()
    {
        var messages = new List<BufferedCriticalMessage>();
        while (_queue.TryDequeue(out var msg))
        {
            Interlocked.Decrement(ref _count);
            messages.Add(msg);
        }
        return messages;
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
