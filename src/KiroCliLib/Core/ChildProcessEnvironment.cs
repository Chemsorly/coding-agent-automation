using System.Diagnostics;

namespace KiroCliLib.Core;

/// <summary>
/// Utilities for controlling what environment variables child processes inherit.
/// </summary>
public static class ChildProcessEnvironment
{
    // W3C Trace Context keys that are not covered by the OTEL_ prefix but must also be stripped.
    private static readonly string[] TelemetryExactKeys = ["TRACEPARENT", "TRACESTATE"];

    /// <summary>
    /// Removes all OpenTelemetry configuration and trace-context variables from the child
    /// process environment captured in <paramref name="psi"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Removes every key whose name starts with <c>OTEL_</c> (case-insensitive) and the
    /// W3C trace-context keys <c>TRACEPARENT</c> and <c>TRACESTATE</c> (case-insensitive).
    /// </para>
    /// <para>
    /// Why enumeration + comparison instead of a direct <c>Remove(key)</c>: on Linux,
    /// environment variable names are case-sensitive.  A caller that sets
    /// <c>otel_service_name</c> in lower-case would be missed by a direct
    /// <c>Remove("OTEL_SERVICE_NAME")</c>.  Enumerating the snapshot and comparing
    /// with <see cref="StringComparison.OrdinalIgnoreCase"/> is safe on both platforms.
    /// </para>
    /// <para>
    /// This method modifies only the <em>copy</em> stored in
    /// <see cref="ProcessStartInfo.Environment"/>.  It never mutates the parent process
    /// environment (<see cref="Environment.GetEnvironmentVariables"/>).
    /// </para>
    /// </remarks>
    /// <param name="psi">The <see cref="ProcessStartInfo"/> whose environment to sanitize.</param>
    public static void StripTelemetry(ProcessStartInfo psi)
    {
        ArgumentNullException.ThrowIfNull(psi);

        // Collect keys to remove in a separate pass to avoid mutating while enumerating.
        // ProcessStartInfo.Environment is IDictionary<string, string?> in .NET 10.
        // TODO: [WARNING] This method is NOT thread-safe with respect to concurrent callers
        // sharing the same ProcessStartInfo instance. The two-pass pattern (enumerate Keys into a
        // List, then Remove) is safe against modification-during-enumeration within a single call,
        // but if two threads call StripTelemetry on the same psi concurrently the underlying
        // Dictionary can be corrupted. All current call sites construct a new ProcessStartInfo
        // immediately before calling this method, so no sharing occurs in practice. If that
        // ever changes, external synchronisation on the psi instance is required.
        var keysToRemove = psi.Environment.Keys
            .Where(k => k.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)
                     || TelemetryExactKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToRemove)
            psi.Environment.Remove(key);
    }
}
