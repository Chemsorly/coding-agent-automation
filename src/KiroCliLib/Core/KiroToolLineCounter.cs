using System.Text.RegularExpressions;

namespace KiroCliLib.Core;

/// <summary>Calls of one MCP or web tool in a kiro-cli run, and how many of them failed.</summary>
public sealed record KiroToolCallCount(int Calls, int Failed);

/// <summary>
/// Counts MCP and web tool calls from kiro-cli's stderr tool lines. A failure is attributed only when
/// exactly one call was outstanding, because the status lines carry no call id.
/// </summary>
public sealed class KiroToolLineCounter
{
    private static readonly Regex McpRunPattern =
        new(@"^\[tool\] Running: @([^/\s]+)/(\S+)$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _calls = new();
    private readonly Dictionary<string, int> _failed = new();
    private int _outstanding;
    private string? _soleKey;

    /// <summary>
    /// Processes one kiro-cli stderr line. Only <c>[tool] </c>-prefixed lines are counted;
    /// all other lines are ignored. Thread-safe.
    /// </summary>
    public void ProcessLine(string cleanLine)
    {
        ArgumentNullException.ThrowIfNull(cleanLine);
        var line = cleanLine.Trim();
        if (!line.StartsWith("[tool] ", StringComparison.Ordinal))
            return;

        lock (_lock)
        {
            if (line.StartsWith("[tool] status:", StringComparison.Ordinal))
            {
                // Status line — ends one outstanding call.
                var statusText = line[(line.IndexOf(':') + 1)..].Trim();
                if (_outstanding == 1 && _soleKey is not null &&
                    statusText.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    _failed[_soleKey] = (_failed.TryGetValue(_soleKey, out var f) ? f : 0) + 1;
                }

                _outstanding = Math.Max(0, _outstanding - 1);
                if (_outstanding == 0)
                    _soleKey = null;
            }
            else
            {
                // Any other [tool] line starts a call.
                string? key = null;
                var match = McpRunPattern.Match(line);
                if (match.Success)
                {
                    key = $"{match.Groups[1].Value}/{match.Groups[2].Value}";
                }
                else if (line.Equals("[tool] Searching the web", StringComparison.Ordinal))
                {
                    key = "web/search";
                }

                if (key is not null)
                    _calls[key] = (_calls.TryGetValue(key, out var c) ? c : 0) + 1;

                _soleKey = _outstanding == 0 ? key : null;
                _outstanding++;
            }
        }
    }

    /// <summary>
    /// Returns a snapshot of the current counts, keyed in ordinal order. Thread-safe.
    /// </summary>
    public IReadOnlyDictionary<string, KiroToolCallCount> Snapshot()
    {
        lock (_lock)
        {
            var sorted = new SortedDictionary<string, KiroToolCallCount>(StringComparer.Ordinal);
            foreach (var kvp in _calls)
                sorted[kvp.Key] = new KiroToolCallCount(kvp.Value, _failed.GetValueOrDefault(kvp.Key, 0));
            return sorted;
        }
    }
}
