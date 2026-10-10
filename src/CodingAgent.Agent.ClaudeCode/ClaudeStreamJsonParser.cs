using System.Globalization;
using System.Text.Json;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;

namespace CodingAgent.Agent.ClaudeCode;

/// <summary>Usage of one model as a Claude Code <c>result</c> event reports it (cumulative per session).</summary>
internal sealed record ClaudeModelTotals
{
    public long InputTokens { get; init; }

    /// <summary>Output tokens, thinking tokens included (as the CLI reports them).</summary>
    public long OutputTokens { get; init; }

    public long ThinkingTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public int WebSearchRequests { get; init; }
    public decimal? CostUsd { get; init; }
}

/// <summary>
/// The usage figures of a Claude Code <c>result</c> event. A resumed session reports the whole
/// conversation's totals, earlier calls included, so the provider subtracts what it saw before.
/// </summary>
internal sealed record ClaudeUsageTotals
{
    public long InputTokens { get; init; }

    /// <summary>Output tokens, thinking tokens included.</summary>
    public long OutputTokens { get; init; }

    public long ThinkingTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public int WebSearchRequests { get; init; }
    public decimal? CostUsd { get; init; }
    public int Turns { get; init; }
    public long ApiDurationMs { get; init; }
    public IReadOnlyDictionary<string, ClaudeModelTotals> Models { get; init; } = new Dictionary<string, ClaudeModelTotals>();

    /// <summary>
    /// Returns this call's share: <c>this - previous</c> per field. A field smaller than before means
    /// the CLI reported that field for this call only, so its current value is kept as-is.
    /// </summary>
    public ClaudeUsageTotals Minus(ClaudeUsageTotals? previous)
    {
        if (previous is null)
            return this;

        return new ClaudeUsageTotals
        {
            InputTokens = Delta(InputTokens, previous.InputTokens),
            OutputTokens = Delta(OutputTokens, previous.OutputTokens),
            ThinkingTokens = Delta(ThinkingTokens, previous.ThinkingTokens),
            CacheReadTokens = Delta(CacheReadTokens, previous.CacheReadTokens),
            CacheWriteTokens = Delta(CacheWriteTokens, previous.CacheWriteTokens),
            WebSearchRequests = (int)Delta(WebSearchRequests, previous.WebSearchRequests),
            CostUsd = Delta(CostUsd, previous.CostUsd),
            Turns = Turns, // num_turns counts this call's turns only, even on a resumed session
            ApiDurationMs = Delta(ApiDurationMs, previous.ApiDurationMs),
            Models = Models.ToDictionary(
                kvp => kvp.Key,
                kvp => previous.Models.TryGetValue(kvp.Key, out var before) ? Minus(kvp.Value, before) : kvp.Value)
        };
    }

    private static ClaudeModelTotals Minus(ClaudeModelTotals current, ClaudeModelTotals previous) => new()
    {
        InputTokens = Delta(current.InputTokens, previous.InputTokens),
        OutputTokens = Delta(current.OutputTokens, previous.OutputTokens),
        ThinkingTokens = Delta(current.ThinkingTokens, previous.ThinkingTokens),
        CacheReadTokens = Delta(current.CacheReadTokens, previous.CacheReadTokens),
        CacheWriteTokens = Delta(current.CacheWriteTokens, previous.CacheWriteTokens),
        WebSearchRequests = (int)Delta(current.WebSearchRequests, previous.WebSearchRequests),
        CostUsd = Delta(current.CostUsd, previous.CostUsd)
    };

    private static long Delta(long current, long previous) => current >= previous ? current - previous : current;

    private static decimal? Delta(decimal? current, decimal? previous)
    {
        if (current is null || previous is null || current < previous)
            return current;
        return current - previous;
    }
}

/// <summary>
/// What the stream of one Claude Code invocation told us. Written by <see cref="ClaudeStreamJsonParser"/>
/// on the process's stdout callback; read after the process exits, except <see cref="TurnInProgress"/>
/// and <see cref="ResultCount"/>, which the provider reads while the CLI runs.
/// </summary>
internal sealed class ClaudeStreamState
{
    /// <summary>Rate-limit window the CLI reports for extra usage beyond the subscription.</summary>
    internal const string OverageWindow = "overage";

    private volatile bool _turnInProgress;
    private int _resultCount;
    private long _lastResultTicks;

    public string? SessionId { get; set; }
    public string? Model { get; set; }
    public bool ResultSeen { get; set; }

    /// <summary>
    /// True from a turn's first event until its <c>result</c>. A <c>claude -p</c> run can start another
    /// turn after its result, e.g. when a background command it started finishes.
    /// </summary>
    public bool TurnInProgress
    {
        get => _turnInProgress;
        set => _turnInProgress = value;
    }

    /// <summary>How many <c>result</c> events the stream has carried.</summary>
    public int ResultCount => Volatile.Read(ref _resultCount);

    /// <summary>Time since the last <c>result</c> event; meaningful once <see cref="ResultCount"/> is above zero.</summary>
    public TimeSpan SinceLastResult =>
        DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastResultTicks), DateTimeKind.Utc);

    /// <summary>
    /// True when the CLI was killed before it could save the session's usage totals, which it does
    /// at exit. A later resumed call then reports totals that lack this call's usage.
    /// </summary>
    public bool StoppedBeforeSaving { get; set; }

    /// <summary>Records a result; called before the turn is marked finished, so a reader never sees a stale time.</summary>
    internal void CountResult()
    {
        Interlocked.Exchange(ref _lastResultTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref _resultCount);
    }
    public bool ResultIsError { get; set; }
    public string? ResultSubtype { get; set; }
    public string? ResultText { get; set; }
    public int? ApiErrorStatus { get; set; }

    /// <summary>Last error category the CLI reported (assistant <c>error</c> or <c>api_retry</c> <c>error</c>).</summary>
    public string? LastErrorCategory { get; set; }

    /// <summary>HTTP status of the last failed API attempt (<c>api_retry</c> <c>error_status</c>).</summary>
    public int? LastErrorStatus { get; set; }

    public ClaudeUsageTotals? Totals { get; set; }

    /// <summary>Latest rate-limit reading per window.</summary>
    public Dictionary<string, AgentRateLimitObservation> RateLimits { get; } = new(StringComparer.Ordinal);

    /// <summary>Classifies a failed invocation as a provider-side error, from what the stream reported.</summary>
    public AgentErrorCategory ClassifyFailure()
    {
        var status = ApiErrorStatus ?? LastErrorStatus;

        if (LastErrorCategory is "authentication_failed" or "oauth_org_not_allowed" or "account_on_hold" or "billing_error"
            || status is 401 or 403)
            return AgentErrorCategory.PermanentAuthFailure;

        // The overage window reads "rejected" on any account without extra usage, even while the
        // subscription window allows requests, so only the subscription windows count here.
        if (LastErrorCategory is "rate_limit" || status is 429
            || RateLimits.Any(r => r.Key != OverageWindow && r.Value.Status == "rejected"))
            return AgentErrorCategory.ProviderRateLimit;

        if (LastErrorCategory is "overloaded" or "server_error" || status is 500 or 502 or 503 or 529)
            return AgentErrorCategory.ProviderOverload;

        return AgentErrorCategory.None;
    }
}

/// <summary>
/// Parses the newline-delimited JSON the Claude Code CLI prints with
/// <c>-p --output-format stream-json --verbose</c>, and turns it into readable output lines.
/// Event shapes: https://code.claude.com/docs/en/headless and the Agent SDK message reference.
/// </summary>
internal static class ClaudeStreamJsonParser
{
    private const int MaxSummaryLength = 200;
    private const string ProviderTag = "claude";
    private const long MaxUnixSeconds = 253_402_300_799;          // 9999-12-31T23:59:59Z
    private const long MaxUnixMilliseconds = 253_402_300_799_999;

    /// <summary>
    /// Updates <paramref name="state"/> from one stdout line and returns the readable lines it
    /// produces: assistant text, one line per tool call, retries and rate-limit warnings.
    /// A line that is not JSON is returned unchanged (ANSI codes stripped).
    /// </summary>
    public static IReadOnlyList<string> ProcessLine(string line, ClaudeStreamState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(line))
            return [];

        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('{'))
            return [AnsiStripper.Strip(line)];

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            return GetString(root, "type") switch
            {
                "system" => ProcessSystem(root, state),
                "assistant" => ProcessAssistant(root, state),
                "result" => ProcessResult(root, state),
                "rate_limit_event" or "rate_limit" => ProcessRateLimit(root, state),
                _ => []
            };
        }
        catch (JsonException)
        {
            return [AnsiStripper.Strip(line)];
        }
    }

    private static IReadOnlyList<string> ProcessSystem(JsonElement root, ClaudeStreamState state)
    {
        switch (GetString(root, "subtype"))
        {
            case "init":
                state.TurnInProgress = true;
                state.SessionId = GetString(root, "session_id") ?? state.SessionId;
                state.Model = GetString(root, "model") ?? state.Model;
                return [];

            case "api_retry":
                var error = GetString(root, "error");
                var status = GetInt(root, "error_status");
                state.LastErrorCategory = error ?? state.LastErrorCategory;
                state.LastErrorStatus = status ?? state.LastErrorStatus;
                var attempt = GetInt(root, "attempt");
                var maxRetries = GetInt(root, "max_retries");
                return [$"⚠ Claude API retry {attempt}/{maxRetries}: {error ?? "unknown"}{(status is null ? "" : $" (HTTP {status})")}"];

            default:
                return [];
        }
    }

    private static List<string> ProcessAssistant(JsonElement root, ClaudeStreamState state)
    {
        state.TurnInProgress = true;
        state.SessionId ??= GetString(root, "session_id");

        // Messages from subagents carry the ID of the tool call that started them. Only the main
        // conversation's API errors describe the call; a main message without one means any
        // earlier retried error was recovered from.
        var isSubagent = GetString(root, "parent_tool_use_id") is not null;
        if (!isSubagent)
        {
            if (GetString(root, "error") is { } error)
            {
                state.LastErrorCategory = error;
            }
            else
            {
                state.LastErrorCategory = null;
                state.LastErrorStatus = null;
            }
        }

        if (!root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            return [];

        var indent = isSubagent ? "  " : "";
        var lines = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            switch (GetString(block, "type"))
            {
                case "text" when GetString(block, "text") is { } text:
                    lines.AddRange(text.ReplaceLineEndings("\n").Split('\n').Select(l => indent + l));
                    break;

                case "tool_use":
                    var name = GetString(block, "name") ?? "tool";
                    var summary = block.TryGetProperty("input", out var input) ? SummarizeToolInput(name, input) : null;
                    lines.Add(string.IsNullOrEmpty(summary) ? $"{indent}▶ {name}" : $"{indent}▶ {name}: {summary}");
                    break;
            }
        }

        return lines;
    }

    private static IReadOnlyList<string> ProcessResult(JsonElement root, ClaudeStreamState state)
    {
        state.ResultSeen = true;
        state.CountResult();
        state.TurnInProgress = false;
        state.SessionId = GetString(root, "session_id") ?? state.SessionId;
        state.ResultIsError = root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True;
        state.ResultSubtype = GetString(root, "subtype");
        state.ResultText = GetString(root, "result");
        state.ApiErrorStatus = GetInt(root, "api_error_status") ?? state.ApiErrorStatus;
        state.Totals = ParseTotals(root);

        if (!state.ResultIsError)
            return [];

        var message = state.ResultText;
        if (string.IsNullOrWhiteSpace(message)
            && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            message = string.Join("; ", errors.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString()));

        return [$"✖ Claude Code failed ({state.ResultSubtype ?? "error"}): {message}"];
    }

    private static List<string> ProcessRateLimit(JsonElement root, ClaudeStreamState state)
    {
        if (!root.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object)
            return [];

        var lines = new List<string>();

        var status = GetString(info, "status");
        if (status is not null)
        {
            var observation = new AgentRateLimitObservation
            {
                Provider = ProviderTag,
                Window = GetString(info, "rateLimitType", "rate_limit_type") ?? "unknown",
                Status = status,
                Utilization = GetDouble(info, "utilization"),
                ResetsAt = GetUnixTime(info, "resetsAt", "resets_at")
            };
            state.RateLimits[observation.Window] = observation;
            if (status != "allowed")
                lines.Add(FormatRateLimit(observation));
        }

        var overageStatus = GetString(info, "overageStatus", "overage_status");
        if (overageStatus is not null)
        {
            state.RateLimits[ClaudeStreamState.OverageWindow] = new AgentRateLimitObservation
            {
                Provider = ProviderTag,
                Window = ClaudeStreamState.OverageWindow,
                Status = overageStatus,
                ResetsAt = GetUnixTime(info, "overageResetsAt", "overage_resets_at")
            };
        }

        return lines;
    }

    private static string FormatRateLimit(AgentRateLimitObservation observation)
    {
        var used = observation.Utilization is { } u ? $", {u.ToString("P0", CultureInfo.InvariantCulture)} used" : "";
        var resets = observation.ResetsAt is { } r ? $", resets {r:u}" : "";
        return $"⚠ Claude subscription limit {observation.Window}: {observation.Status}{used}{resets}";
    }

    private static ClaudeUsageTotals ParseTotals(JsonElement result)
    {
        var models = new Dictionary<string, ClaudeModelTotals>(StringComparer.Ordinal);
        if ((result.TryGetProperty("modelUsage", out var modelUsage) || result.TryGetProperty("model_usage", out modelUsage))
            && modelUsage.ValueKind == JsonValueKind.Object)
        {
            foreach (var model in modelUsage.EnumerateObject())
            {
                var m = model.Value;
                models[model.Name] = new ClaudeModelTotals
                {
                    InputTokens = GetLong(m, "inputTokens", "input_tokens"),
                    OutputTokens = GetLong(m, "outputTokens", "output_tokens"),
                    ThinkingTokens = GetLong(m, "thinkingTokens", "thinking_tokens"),
                    CacheReadTokens = GetLong(m, "cacheReadInputTokens", "cache_read_input_tokens"),
                    CacheWriteTokens = GetLong(m, "cacheCreationInputTokens", "cache_creation_input_tokens"),
                    WebSearchRequests = (int)GetLong(m, "webSearchRequests", "web_search_requests"),
                    CostUsd = GetDecimal(m, "costUSD", "cost_usd")
                };
            }
        }

        var totals = new ClaudeUsageTotals
        {
            CostUsd = GetDecimal(result, "total_cost_usd"),
            Turns = (int)GetLong(result, "num_turns"),
            ApiDurationMs = GetLong(result, "duration_api_ms"),
            Models = models
        };

        // modelUsage covers every model call (main loop, subagents, compaction); usage only the
        // main loop. Prefer the former and fall back to the latter on CLI versions without it, or
        // when modelUsage carries no token counts at all.
        var modelTokens = models.Values.Sum(m => m.InputTokens + m.OutputTokens + m.CacheReadTokens + m.CacheWriteTokens);
        if (modelTokens > 0)
        {
            return totals with
            {
                InputTokens = models.Values.Sum(m => m.InputTokens),
                OutputTokens = models.Values.Sum(m => m.OutputTokens),
                ThinkingTokens = models.Values.Sum(m => m.ThinkingTokens),
                CacheReadTokens = models.Values.Sum(m => m.CacheReadTokens),
                CacheWriteTokens = models.Values.Sum(m => m.CacheWriteTokens),
                WebSearchRequests = models.Values.Sum(m => m.WebSearchRequests)
            };
        }

        if (!result.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return totals;

        var webSearches = usage.TryGetProperty("server_tool_use", out var serverToolUse) && serverToolUse.ValueKind == JsonValueKind.Object
            ? (int)GetLong(serverToolUse, "web_search_requests")
            : 0;

        return totals with
        {
            InputTokens = GetLong(usage, "input_tokens"),
            OutputTokens = GetLong(usage, "output_tokens"),
            CacheReadTokens = GetLong(usage, "cache_read_input_tokens"),
            CacheWriteTokens = GetLong(usage, "cache_creation_input_tokens"),
            WebSearchRequests = webSearches
        };
    }

    /// <summary>Picks the most telling input field of a tool call for a one-line summary.</summary>
    internal static string? SummarizeToolInput(string toolName, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return null;

        string[] preferredKeys = toolName switch
        {
            "Bash" or "PowerShell" => ["command"],
            "Read" or "Write" or "Edit" or "MultiEdit" or "NotebookEdit" => ["file_path", "notebook_path"],
            "Grep" or "Glob" => ["pattern"],
            "WebFetch" => ["url"],
            "WebSearch" => ["query"],
            "Task" or "Agent" => ["description"],
            _ => []
        };

        var value = preferredKeys.Select(k => GetString(input, k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            ?? input.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .Select(p => p.Value.GetString())
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        if (value is null)
            return null;

        var firstLine = value.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return firstLine.Length <= MaxSummaryLength ? firstLine : firstLine[..MaxSummaryLength] + "…";
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)
            ? i
            : null;

    private static long GetLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                return value.TryGetInt64(out var l) ? l : (long)value.GetDouble();
        }
        return 0;
    }

    private static double? GetDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                return value.GetDouble();
        }
        return null;
    }

    private static decimal? GetDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                return value.TryGetDecimal(out var d) ? d : (decimal)value.GetDouble();
        }
        return null;
    }

    private static DateTimeOffset? GetUnixTime(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var timestamp) && timestamp > 0)
            {
                // Documented as Unix seconds; tolerate milliseconds rather than throw on them.
                return timestamp > MaxUnixSeconds
                    ? DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(timestamp, MaxUnixMilliseconds))
                    : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            }
            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
        }
        return null;
    }
}
