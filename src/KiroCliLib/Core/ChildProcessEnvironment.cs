using System.Diagnostics;

namespace KiroCliLib.Core;

/// <summary>
/// Utilities for controlling what environment variables child processes inherit.
/// </summary>
public static class ChildProcessEnvironment
{
    // W3C Trace Context keys that are not covered by the OTEL_ prefix but must also be stripped.
    private static readonly string[] TelemetryExactKeys = ["TRACEPARENT", "TRACESTATE"];

    // Pipeline-owned LLM credentials injected into agent pods. Only the Claude Code provider hands
    // them on (under the CLI's own variable names) to the claude process; every other child process
    // — quality gates, setup commands, git — must not inherit them.
    // KiroCliLib cannot reference CodingAgent.Contracts (circular dependency), so these mirror
    // AgentDefaults.EnvClaudeApiKey and AgentDefaults.EnvClaudeOAuthToken; keep them in sync.
    internal static readonly string[] PipelineCredentialKeys = ["AGENT_CLAUDE_API_KEY", "AGENT_CLAUDE_OAUTH_TOKEN"];

    /// <summary>
    /// Removes all OpenTelemetry configuration and trace-context variables, and the pipeline-owned
    /// LLM credentials, from the child process environment captured in <paramref name="psi"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Removes every key whose name starts with <c>OTEL_</c> (case-insensitive), the
    /// W3C trace-context keys <c>TRACEPARENT</c> and <c>TRACESTATE</c> (case-insensitive), and
    /// <c>AGENT_CLAUDE_API_KEY</c> / <c>AGENT_CLAUDE_OAUTH_TOKEN</c>.
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
                     || TelemetryExactKeys.Contains(k, StringComparer.OrdinalIgnoreCase)
                     || PipelineCredentialKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToRemove)
            psi.Environment.Remove(key);
    }
}
