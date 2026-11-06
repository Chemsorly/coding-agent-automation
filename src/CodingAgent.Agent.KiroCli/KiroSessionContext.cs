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

    /// <summary>The directory kiro-cli writes its session files to.</summary>
    internal static string SessionsDirectory(string? kiroHome, string userHome) =>
        Path.Combine(string.IsNullOrWhiteSpace(kiroHome) ? Path.Combine(userHome, ".kiro") : kiroHome, "sessions", "cli");

    /// <summary>The directory for this process's environment.</summary>
    internal static string SessionsDirectory() =>
        SessionsDirectory(Environment.GetEnvironmentVariable("KIRO_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// The fullest context among <paramref name="workspacePath"/>'s sessions written since <paramref name="since"/>,
    /// counting only turns that ended since then. Parallel calls in one workspace can see each other's sessions;
    /// each then reports the fullest of them, which leaves a phase's peak (a maximum) unchanged.
    /// </summary>
    internal static Reading? Read(string sessionsDirectory, string workspacePath, DateTimeOffset since)
    {
        if (!Directory.Exists(sessionsDirectory)) return null;

        Reading? fullest = null;
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

            if (Parse(json, workspacePath, since) is { } reading
                && (fullest is null || Share(reading) > Share(fullest)))
            {
                fullest = reading;
            }
        }
        return fullest;
    }

    /// <summary>
    /// The largest context of the turns in one session file that ended since <paramref name="since"/>, or null when
    /// the session belongs to another workspace or the file lacks the share or the window.
    /// </summary>
    internal static Reading? Parse(string json, string workspacePath, DateTimeOffset since)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("cwd", out var cwd) || cwd.ValueKind != JsonValueKind.String
                || !SamePath(cwd.GetString()!, workspacePath)
                || !root.TryGetProperty("session_state", out var state) || state.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (WindowTokens(state) is not { } window) return null;
            if (PeakShare(state, since) is not { } share) return null;

            return new Reading((long)Math.Round(share / 100.0 * window, MidpointRounding.AwayFromZero), window);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? WindowTokens(JsonElement state) =>
        state.TryGetProperty("rts_model_state", out var model) && model.ValueKind == JsonValueKind.Object
        && model.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object
        && info.TryGetProperty("context_window_tokens", out var window) && window.ValueKind == JsonValueKind.Number
        && window.TryGetInt64(out var tokens) && tokens > 0
            ? tokens
            : null;

    private static double? PeakShare(JsonElement state, DateTimeOffset since)
    {
        if (!state.TryGetProperty("conversation_metadata", out var meta) || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty("user_turn_metadatas", out var turns) || turns.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        double? peak = null;
        foreach (var turn in turns.EnumerateArray())
        {
            if (turn.ValueKind != JsonValueKind.Object || !EndedSince(turn, since)) continue;
            foreach (var name in (ReadOnlySpan<string>)["context_usage_percentage", "final_context_usage_percentage"])
            {
                if (turn.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                    && value.GetDouble() is var share and > 0)
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

    private static double Share(Reading reading) => (double)reading.PeakTokens / reading.WindowTokens;
}
