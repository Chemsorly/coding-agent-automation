using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Fixed-capacity circular buffer for pipeline output lines.
/// Thread-safe via <c>lock</c>. Oldest lines are discarded when capacity is exceeded.
/// </summary>
public sealed class OutputRingBuffer
{
    private readonly string[] _buffer;
    private int _head;
    private int _count;
    private readonly Lock _lock = new();

    public OutputRingBuffer(int capacity = PipelineConstants.DefaultOutputBufferCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        _buffer = new string[capacity];
    }

    /// <summary>Gets the maximum number of lines this buffer can hold.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Gets the current number of lines in the buffer.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Adds a single line to the buffer. If the buffer is full, the oldest line is discarded.
    /// </summary>
    public void Add(string line)
    {
        lock (_lock)
        {
            var index = (_head + _count) % _buffer.Length;

            if (_count == _buffer.Length)
            {
                // Buffer is full — overwrite oldest and advance head
                _buffer[_head] = line;
                _head = (_head + 1) % _buffer.Length;
            }
            else
            {
                _buffer[index] = line;
                _count++;
            }
        }
    }

    /// <summary>
    /// Adds multiple lines to the buffer. Oldest lines are discarded when capacity is exceeded.
    /// </summary>
    public void AddRange(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        lock (_lock)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var index = (_head + _count) % _buffer.Length;

                if (_count == _buffer.Length)
                {
                    _buffer[_head] = lines[i];
                    _head = (_head + 1) % _buffer.Length;
                }
                else
                {
                    _buffer[index] = lines[i];
                    _count++;
                }
            }
        }
    }

    /// <summary>
    /// Returns all lines in the buffer in insertion order (oldest first).
    /// </summary>
    public IReadOnlyList<string> GetAll()
    {
        lock (_lock)
        {
            var result = new string[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = _buffer[(_head + i) % _buffer.Length];
            }
            return result;
        }
    }
}
