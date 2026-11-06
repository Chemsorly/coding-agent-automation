using System.Globalization;
using System.Text.Json;

namespace CodingAgent.Agent.KiroCli;

/// <summary>
/// How full the context got in a kiro-cli call, read from the V2 engine's session files
/// (<c>~/.kiro/sessions/cli/&lt;id&gt;.json</c>, or <c>$KIRO_HOME/sessions/cli</c>). kiro-cli prints no token or
/// context figures in <c>--no-interactive</c> mode, but each turn's metadata in the session file carries the share
/// of the context window it used (<c>context_usage_percentage</c>, 0–100), and the model state carries the window.
/// </summary>
/// <remarks>
/// The file format is undocumented. Field names come from session files captured from kiro-cli 2.x by other
/// tools; not yet verified against a run of ours. Anything missing or shaped differently reads as unknown (null),
/// never as zero.
/// </remarks>
internal static class KiroSessionContext
{
    /// <summary>Largest context of the call in tokens, and the model's context window.</summary>
    internal sealed record Reading(long PeakTokens, long WindowTokens);

    /// <summary>A session of the workspace with turns that ended during the call, and its reading (<see cref="Context"/>) if it has one.</summary>
    internal sealed record Candidate(string? SessionId, Reading? Context);

    /// <summary>The directory kiro-cli writes its session files to.</summary>
    internal static string SessionsDirectory(string? kiroHome, string userHome) =>
        Path.Combine(string.IsNullOrWhiteSpace(kiroHome) ? Path.Combine(userHome, ".kiro") : kiroHome, "sessions", "cli");

    /// <summary>The directory for this process's environment.</summary>
    internal static string SessionsDirectory() =>
        SessionsDirectory(Environment.GetEnvironmentVariable("KIRO_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// The reading of the call's session: the one with <paramref name="sessionId"/> when that is among the
    /// workspace's sessions with turns since <paramref name="since"/>; otherwise the only such session. Null when
    /// several calls ran in the workspace at once and the call's session is not known, so a parallel call never
    /// reports another's context.
    /// </summary>
    internal static Reading? Read(string sessionsDirectory, string workspacePath, DateTimeOffset since, string? sessionId)
    {
        if (!Directory.Exists(sessionsDirectory)) return null;

        var candidates = new List<Candidate>();
        // A second of slack for file systems with coarse modification times.
        var modifiedAfter = since.UtcDateTime.AddSeconds(-1);
        foreach (var file in Directory.EnumerateFiles(sessionsDirectory, "*.json"))
        {
            string json;
            try
            {
                if (File.GetLastWriteTimeUtc(file) < modifiedAfter) continue;
                json = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (Inspect(json, workspacePath, since) is { } candidate)
                candidates.Add(candidate);
        }

        if (sessionId is not null && candidates.Find(c => c.SessionId == sessionId) is { } known)
            return known.Context;
        return candidates.Count == 1 ? candidates[0].Context : null;
    }

    /// <summary>
    /// One session file: a candidate when it belongs to <paramref name="workspacePath"/> and has turns that ended
    /// since <paramref name="since"/>, with the largest context of those turns when the file holds the share and the
    /// window. Null for another workspace, no turns in the call, or a file of another shape.
    /// </summary>
    internal static Candidate? Inspect(string json, string workspacePath, DateTimeOffset since)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("cwd", out var cwd) || cwd.ValueKind != JsonValueKind.String
                || !SamePath(cwd.GetString()!, workspacePath)
                || !root.TryGetProperty("session_state", out var state) || state.ValueKind != JsonValueKind.Object
                || TurnsSince(state, since) is not { Count: > 0 } turns)
            {
                return null;
            }

            var sessionId = root.TryGetProperty("session_id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
            var reading = ContextWindowOf(state) is { } window && PeakShare(turns) is { } share
                ? new Reading((long)Math.Round(share / 100.0 * window, MidpointRounding.AwayFromZero), window)
                : null;
            return new Candidate(sessionId, reading);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? ContextWindowOf(JsonElement state) =>
        state.TryGetProperty("rts_model_state", out var model) && model.ValueKind == JsonValueKind.Object
        && model.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object
        && info.TryGetProperty("context_window_tokens", out var window) && window.ValueKind == JsonValueKind.Number
        && window.TryGetInt64(out var tokens) && tokens > 0
            ? tokens
            : null;

    private static List<JsonElement>? TurnsSince(JsonElement state, DateTimeOffset since)
    {
        if (!state.TryGetProperty("conversation_metadata", out var meta) || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty("user_turn_metadatas", out var turns) || turns.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        return turns.EnumerateArray()
            .Where(turn => turn.ValueKind == JsonValueKind.Object && EndedSince(turn, since))
            .ToList();
    }

    private static readonly string[] ShareFields = ["context_usage_percentage", "final_context_usage_percentage"];

    private static double? PeakShare(List<JsonElement> turns)
    {
        double? peak = null;
        foreach (var turn in turns)
        {
            foreach (var name in ShareFields)
            {
                if (turn.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                    && value.TryGetDouble(out var share) && double.IsFinite(share) && share > 0)
                {
                    peak = Math.Max(peak ?? 0, share);
                }
            }
        }
        return peak;
    }

    private static bool EndedSince(JsonElement turn, DateTimeOffset since) =>
        turn.TryGetProperty("end_timestamp", out var end) && end.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(end.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ended)
        && ended >= since;

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.Ordinal);
}
