namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Pure logic circuit breaker: evaluates per-template failure counts against a threshold.
/// No I/O, no async, no DI — independently unit-testable.
///
/// Thread safety: This class is NOT thread-safe. The caller (PipelineLoopService) is responsible
/// for serializing access via its existing lock(_lock). All calls to Evaluate/Trip/Reset happen
/// either within the lock or on the single loop thread.
/// </summary>
internal sealed class TemplateCircuitBreaker
{
    /// <summary>Whether the circuit breaker is currently tripped.</summary>
    public bool IsTripped { get; private set; }

    /// <summary>Error message associated with the trip, or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>Timestamp when the circuit was tripped, or null if not tripped.</summary>
    public DateTimeOffset? TrippedAt { get; private set; }

    /// <summary>
    /// Evaluates whether the circuit should trip based on per-template failure counts.
    /// Returns true if the trip condition is met (all templates at or above threshold).
    ///
    /// This is a PURE QUERY — it does NOT mutate state. The caller must call <see cref="Trip"/>
    /// separately to actually transition state. This separation exists because PipelineLoopService
    /// needs to perform the state transition inside its lock(_lock) block alongside _resumeSignal
    /// creation. Safe from TOCTOU because CheckCircuitBreakerAsync runs on a single loop thread
    /// and the failure counts cannot change between Evaluate() and Trip().
    /// </summary>
    /// <param name="templateFailureCounts">Per-template consecutive failure counts.</param>
    /// <param name="threshold">Number of consecutive failures required to trip.</param>
    /// <returns>True if the trip condition is met; false otherwise.</returns>
    // TODO: Validate templateFailureCounts for null (throw ArgumentNullException) instead of allowing NullReferenceException on .Count
    public bool Evaluate(IReadOnlyDictionary<string, int> templateFailureCounts, int threshold)
    {
        if (IsTripped) return false; // Already tripped — don't re-trip
        if (templateFailureCounts.Count == 0) return false; // No templates → never trips

        return templateFailureCounts.Values.All(f => f >= threshold);
    }

    /// <summary>
    /// Marks the circuit as tripped with a timestamp and optional error message.
    /// Called by PipelineLoopService inside lock(_lock) when <see cref="Evaluate"/> returns true.
    /// </summary>
    /// <param name="error">Optional error message to associate with the trip.</param>
    public void Trip(string? error = null)
    {
        IsTripped = true;
        TrippedAt = DateTimeOffset.UtcNow;
        LastError = error;
    }

    /// <summary>
    /// Returns true if the cooldown duration has elapsed since the circuit was tripped.
    /// Provided for API completeness — the current integration uses Task.Delay-based waiting
    /// rather than polling ShouldAutoResume(), but this enables alternative consumption patterns.
    /// </summary>
    /// <param name="cooldown">The cooldown duration after which auto-resume is allowed.</param>
    /// <returns>True if tripped and cooldown has elapsed; false otherwise.</returns>
    public bool ShouldAutoResume(TimeSpan cooldown)
    {
        if (!IsTripped) return false;
        if (!TrippedAt.HasValue) return false;

        return DateTimeOffset.UtcNow - TrippedAt.Value >= cooldown;
    }

    /// <summary>
    /// Resets all state: IsTripped=false, LastError=null, TrippedAt=null.
    /// Called on manual ResumeLoop(), auto-resume, CleanupAsync(), and StartLoopAsync().
    /// </summary>
    public void Reset()
    {
        IsTripped = false;
        LastError = null;
        TrippedAt = null;
    }
}
