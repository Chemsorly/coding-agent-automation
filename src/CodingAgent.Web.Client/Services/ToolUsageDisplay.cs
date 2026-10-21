using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Services;

/// <summary>One MCP server (or "web") in a run: its calls summed over phases, and whether it was unavailable.</summary>
public sealed record ServerToolUsage(
    string Server,
    McpCallCounts Totals,
    string? Unavailable,
    IReadOnlyList<(string Tool, McpCallCounts Counts)> Tools);

public static class ToolUsageDisplay
{
    /// <summary>
    /// Every server of the run: configured (<see cref="PipelineRunSummary.McpServers"/>), called (any phase's
    /// ToolCalls) or unavailable (any phase's UnavailableMcpServers). Includes "web" when it was called.
    /// Sorted by calls, most first, then by name (ordinal, ignore case). Empty for runs without data.
    /// </summary>
    public static IReadOnlyList<ServerToolUsage> ByServer(PipelineRunSummary? run, IReadOnlyCollection<string>? phases = null)
    {
        if (run is null) return [];

        var hasAnyData = run.McpServers is { Count: > 0 }
            || run.PhaseBreakdown?.Values.Any(p => p.ToolCalls?.Count > 0 || p.UnavailableMcpServers?.Count > 0) == true;
        if (!hasAnyData) return [];

        var phaseFilter = phases is not null
            ? new HashSet<string>(phases, StringComparer.OrdinalIgnoreCase)
            : null;

        // Display casing: configured McpServers wins; otherwise first-seen casing from ToolCalls/UnavailableMcpServers.
        var serverCasing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var serverTotals = new Dictionary<string, McpCallCounts>(StringComparer.OrdinalIgnoreCase);
        var serverTools = new Dictionary<string, Dictionary<string, McpCallCounts>>(StringComparer.OrdinalIgnoreCase);
        var serverUnavailable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Seed from configured servers (sets display casing and ensures zero-call entries appear).
        if (run.McpServers is not null)
        {
            foreach (var name in run.McpServers)
            {
                if (!serverCasing.ContainsKey(name))
                    serverCasing[name] = name;
                serverTotals.TryAdd(name, new McpCallCounts());
                serverTools.TryAdd(name, new Dictionary<string, McpCallCounts>(StringComparer.OrdinalIgnoreCase));
            }
        }

        if (run.PhaseBreakdown is not null)
        {
            foreach (var (phaseKey, phaseUsage) in run.PhaseBreakdown)
            {
                if (phaseFilter is not null && !phaseFilter.Contains(phaseKey))
                    continue;

                if (phaseUsage.ToolCalls is not null)
                {
                    foreach (var (toolKey, counts) in phaseUsage.ToolCalls)
                    {
                        var server = McpToolKeys.ServerOf(toolKey);
                        var tool = toolKey.Length > server.Length + 1
                            ? toolKey[(server.Length + 1)..]
                            : toolKey;

                        if (!serverCasing.ContainsKey(server))
                            serverCasing[server] = server;
                        serverTotals.TryAdd(server, new McpCallCounts());
                        serverTools.TryAdd(server, new Dictionary<string, McpCallCounts>(StringComparer.OrdinalIgnoreCase));

                        serverTotals[server] = serverTotals[server].Add(counts);

                        var toolMap = serverTools[server];
                        toolMap.TryAdd(tool, new McpCallCounts());
                        toolMap[tool] = toolMap[tool].Add(counts);
                    }
                }

                if (phaseUsage.UnavailableMcpServers is not null)
                {
                    foreach (var (server, status) in phaseUsage.UnavailableMcpServers)
                    {
                        if (!serverCasing.ContainsKey(server))
                            serverCasing[server] = server;
                        serverTotals.TryAdd(server, new McpCallCounts());
                        serverTools.TryAdd(server, new Dictionary<string, McpCallCounts>(StringComparer.OrdinalIgnoreCase));

                        // needs-auth wins over failed when phases disagree.
                        if (!serverUnavailable.TryGetValue(server, out var existing)
                            || existing != McpServerStatuses.NeedsAuth)
                        {
                            serverUnavailable[server] = status;
                        }
                    }
                }
            }
        }

        return serverCasing.Values
            .Select(displayName =>
            {
                var totals = serverTotals.TryGetValue(displayName, out var t) ? t : new McpCallCounts();
                var unavailable = serverUnavailable.TryGetValue(displayName, out var u) ? u : null;

                IReadOnlyList<(string Tool, McpCallCounts Counts)> tools = serverTools.TryGetValue(displayName, out var toolMap)
                    ? toolMap.OrderByDescending(kv => kv.Value.Calls)
                              .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                              .Select(kv => (Tool: kv.Key, Counts: kv.Value))
                              .ToList()
                    : [];

                return new ServerToolUsage(displayName, totals, unavailable, tools);
            })
            .OrderByDescending(s => s.Totals.Calls)
            .ThenBy(s => s.Server, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"sonarqube 20 · web 2" for one phase's ToolCalls, servers by calls; "" when none.</summary>
    public static string PhaseSummary(PhaseUsage? phase)
    {
        if (phase is null || phase.ToolCalls is null || phase.ToolCalls.Count == 0)
            return "";

        var serverCalls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var serverCasing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, counts) in phase.ToolCalls)
        {
            var server = McpToolKeys.ServerOf(key);
            if (!serverCasing.ContainsKey(server))
                serverCasing[server] = server;
            serverCalls[server] = serverCalls.GetValueOrDefault(server, 0) + counts.Calls;
        }

        return string.Join(" · ", serverCalls
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{serverCasing.GetValueOrDefault(kv.Key, kv.Key)} {kv.Value}"));
    }

    /// <summary>"connection 1, auth 0, other 2, unknown 0" for a tooltip.</summary>
    public static string FailureBreakdown(McpCallCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return $"connection {counts.FailedConnection}, auth {counts.FailedAuth}, other {counts.FailedOther}, unknown {counts.FailedUnknown}";
    }
}
