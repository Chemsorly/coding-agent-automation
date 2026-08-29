namespace CodingAgentWebUI.Services;

/// <summary>
/// Bridges <see cref="NotificationService"/> to <see cref="IFaroService"/>, forwarding
/// notifications to Grafana Faro so they appear in frontend observability dashboards.
///
/// Error notifications → Faro error (visible in error tracking dashboards).
/// Info/Success notifications → Faro log (visible in log streams).
///
/// This is registered as Scoped alongside NotificationService (one per Blazor circuit).
/// Call <see cref="FlushAsync"/> after adding notifications to push them to Faro.
/// Typically called from component lifecycle hooks (e.g. after a pipeline dispatch).
///
/// Threading: FlushAsync is not thread-safe and must be called from the Blazor circuit
/// dispatcher (i.e., from component lifecycle methods or InvokeAsync). It is not safe
/// to call concurrently from multiple threads.
/// </summary>
public sealed class NotificationFaroBridge(NotificationService notifications, IFaroService faro)
{
    // Timestamp watermark: we remember the timestamp of the newest entry we have already
    // forwarded. On each flush we take all entries newer than the watermark.
    //
    // Why timestamps instead of a list-count?
    // NotificationService is a ring buffer capped at MaxEntries (50). Once full it evicts
    // the oldest entry on every Add(), so all.Count stabilises at 50. A count-based
    // watermark would reach 50 and never advance — every new entry would be silently
    // dropped. A timestamp survives the eviction because it is tied to message content,
    // not list position.
    //
    // Collision edge case: two entries with identical Timestamp (DateTime.UtcNow, ms
    // precision) are treated as "same instant" — if the first lands on the watermark the
    // second would be skipped. In practice NotificationService calls DateTime.UtcNow in
    // the constructor; two notifications in the same millisecond is theoretically possible
    // but rare and acceptable for an observability bridge.
    private DateTime _watermark = DateTime.MinValue;

    /// <summary>
    /// Pushes any notifications added since the last flush to Faro.
    /// Safe to call multiple times; only new entries are forwarded.
    /// </summary>
    public async Task FlushAsync()
    {
        var all = notifications.GetAll(); // newest-first

        // Select entries strictly newer than the watermark.
        var newEntries = all
            .Where(e => e.Timestamp > _watermark)
            .ToList();

        if (newEntries.Count == 0)
            return;

        // Advance watermark to the newest entry we are about to forward.
        // newEntries[0] is the newest (GetAll returns newest-first).
        _watermark = newEntries[0].Timestamp;

        // Forward in chronological order (oldest-first) so Faro log stream is readable.
        newEntries.Reverse();
        foreach (var entry in newEntries)
        {
            if (entry.Severity == NotificationSeverity.Error)
                await faro.PushErrorAsync($"[notification] {entry.Message}");
            else
                await faro.PushLogAsync($"[notification] {entry.Message}", "info");
        }
    }
}
