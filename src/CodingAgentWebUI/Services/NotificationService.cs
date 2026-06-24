namespace CodingAgentWebUI.Services;

public enum NotificationSeverity { Success, Error, Info }

public sealed record NotificationEntry(string Message, NotificationSeverity Severity, DateTime Timestamp);

/// <summary>
/// Stores notification history in-memory (ring buffer, last 50).
/// Registered as scoped (per Blazor circuit) so history persists across page navigation within a session.
/// </summary>
public sealed class NotificationService
{
    private const int MaxEntries = 50;
    private readonly object _lock = new();
    private readonly LinkedList<NotificationEntry> _entries = new();
    private int _unreadCount;

    public int UnreadCount { get { lock (_lock) { return _unreadCount; } } }

    public void Add(string message, NotificationSeverity severity)
    {
        lock (_lock)
        {
            _entries.AddFirst(new NotificationEntry(message, severity, DateTime.UtcNow));
            if (_entries.Count > MaxEntries)
                _entries.RemoveLast();
            _unreadCount++;
        }
        OnChange?.Invoke();
    }

    public IReadOnlyList<NotificationEntry> GetAll()
    {
        lock (_lock) { return _entries.ToList(); }
    }

    public void MarkAllRead()
    {
        bool changed;
        lock (_lock) { changed = _unreadCount != 0; _unreadCount = 0; }
        if (changed) OnChange?.Invoke();
    }

    public event Action? OnChange;
}
