using CodingAgent.Infrastructure.Common;
using CodingAgent.Pipeline.Models;
using Serilog;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Structured log helpers for MCP and web tool call events. Both methods emit
/// <see cref="LogEventLevel.Warning"/> entries so operators can query them in Grafana.
/// Error texts are masked and cut before logging — arguments, results and raw error payloads
/// are never stored.
/// </summary>
public static class McpCallLog
{
    /// <summary>Error texts in the log are cut to this many characters, after masking.</summary>
    public const int MaxErrorLength = 200;

    /// <summary>
    /// Logs a failed MCP or web tool call. The error text is first masked with
    /// <see cref="SecretMasker.Mask"/> then cut to <see cref="MaxErrorLength"/> characters.
    /// Masking before cutting ensures a secret that straddles the cut boundary is never
    /// half-logged. Masking is always applied even when <paramref name="secrets"/> is null
    /// (the empty collection causes no replacements), so a provider that accidentally passes null
    /// does not skip masking of any secrets that happen to appear in the error text.
    /// </summary>
    public static void Failed(ILogger logger, string server, string tool,
        McpFailureCategory category, string? error,
        IReadOnlyDictionary<string, string>? secrets)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var text = error ?? string.Empty;
        // Always mask — even with an empty/null secrets dictionary — so the masking step is never
        // silently skipped when a caller passes null.
        IEnumerable<KeyValuePair<string, string>> effectiveSecrets =
            secrets is not null ? secrets : Array.Empty<KeyValuePair<string, string>>();
        text = SecretMasker.Mask(text, effectiveSecrets);
        if (text.Length > MaxErrorLength)
            text = text[..MaxErrorLength] + "…";
        logger.Warning("MCP call failed: {McpServer}/{McpTool} ({McpFailureCategory}): {McpError}",
            server, tool, category, text);
    }

    /// <summary>
    /// Logs an MCP server that could not be used during the run.
    /// </summary>
    public static void Unavailable(ILogger logger, string server, string status)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Warning("MCP server unavailable: {McpServer} ({McpServerStatus})", server, status);
    }
}
