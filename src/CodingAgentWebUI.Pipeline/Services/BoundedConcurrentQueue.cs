using System.Collections;
using System.Collections.Concurrent;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// A thread-safe bounded queue that discards oldest entries when capacity is exceeded.
/// Uses lock-free drain-after-enqueue: the count may momentarily exceed capacity by
/// the number of concurrent writers, which is acceptable for a best-effort memory bound.
/// </summary>
public sealed class BoundedConcurrentQueue<T> : IEnumerable<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly int _capacity;

    public BoundedConcurrentQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        _capacity = capacity;
    }

    /// <summary>Gets the maximum number of items this queue can hold.</summary>
    public int Capacity => _capacity;

    /// <summary>Gets the current number of items in the queue.</summary>
    public int Count => _queue.Count;

    /// <summary>Gets whether the queue is empty.</summary>
    public bool IsEmpty => _queue.IsEmpty;

    /// <summary>
    /// Adds an item to the queue. If capacity is exceeded, oldest items are discarded.
    /// </summary>
    public void Enqueue(T item)
    {
        _queue.Enqueue(item);

        while (_queue.Count > _capacity)
            _queue.TryDequeue(out _);
    }

    /// <summary>
    /// Attempts to remove and return the item at the beginning of the queue.
    /// Returns <c>true</c> if an item was successfully dequeued; <c>false</c> if the queue was empty.
    /// Thread-safe: delegates directly to the underlying <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/>.
    /// </summary>
    public bool TryDequeue(out T? item) => _queue.TryDequeue(out item);

    public IEnumerator<T> GetEnumerator() => _queue.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
