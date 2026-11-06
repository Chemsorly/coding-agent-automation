using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Agent.KiroCli;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Reading how full the context got from kiro-cli's V2 session files (<c>~/.kiro/sessions/cli/&lt;id&gt;.json</c>).
/// The shape follows session files captured from kiro-cli 2.x: per-turn <c>context_usage_percentage</c> (0–100)
/// under <c>session_state.conversation_metadata.user_turn_metadatas</c>, and the window under
/// <c>session_state.rts_model_state.model_info.context_window_tokens</c>.
/// </summary>
public sealed class KiroSessionContextTests : IDisposable
{
    private const string Workspace = "/home/ubuntu/work/repo";
    private static readonly DateTimeOffset CallStart = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiro-sessions-" + Guid.NewGuid().ToString("N"));

    public KiroSessionContextTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Number(double? value) =>
        value is null ? "null" : value.Value.ToString(CultureInfo.InvariantCulture);

    internal static string Turn(DateTimeOffset end, double? percent, double? finalPercent = null) =>
        $$"""
        {
          "end_reason": "UserTurnEnd",
          "end_timestamp": "{{end.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffffZ}}",
          "input_token_count": 0,
          "output_token_count": 0,
          "context_usage_percentage": {{Number(percent)}},
          "final_context_usage_percentage": {{Number(finalPercent)}},
          "metering_usage": []
        }
        """;

    internal static string Session(string cwd, long? window, string id, params string[] turns) =>
        $$"""
        {
          "session_id": "{{id}}",
          "cwd": {{JsonSerializer.Serialize(cwd)}},
          "session_state": {
            "version": "v1",
            "conversation_metadata": { "user_turn_metadatas": [ {{string.Join(",", turns)}} ] },
            "rts_model_state": {
              "model_info": {{(window is null ? "null" : $$"""{ "model_id": "auto", "context_window_tokens": {{window}} }""")}},
              "context_usage_percentage": null
            }
          }
        }
        """;

    private static string AnySession(string cwd, long? window, params string[] turns) => Session(cwd, window, "s1", turns);

    private string WriteSession(string name, string json)
    {
        var path = Path.Combine(_dir, name + ".json");
        File.WriteAllText(path, json);
        File.SetLastWriteTimeUtc(path, CallStart.UtcDateTime.AddMinutes(5));
        return path;
    }

    private static KiroSessionContext.Reading? ReadingOf(string json) =>
        KiroSessionContext.Inspect(json, Workspace, CallStart)?.Context;

    // ── Inspect: one file ───────────────────────────────────────────────────

    [Fact]
    public void Inspect_WhenATurnOfTheCallReportsItsShare_ShouldTurnItIntoTokensOfTheWindow()
    {
        var json = Session(Workspace, 200_000, "abc", Turn(CallStart.AddMinutes(1), 21.5));

        var candidate = KiroSessionContext.Inspect(json, Workspace, CallStart);

        candidate.Should().Be(new KiroSessionContext.Candidate("abc", new KiroSessionContext.Reading(43_000, 200_000)));
    }

    [Fact]
    public void Inspect_ShouldIgnoreTurnsThatEndedBeforeTheCall()
    {
        // A resumed session keeps the turns of earlier phases.
        var json = AnySession(Workspace, 200_000,
            Turn(CallStart.AddMinutes(-30), 80),
            Turn(CallStart.AddMinutes(2), 10));

        ReadingOf(json)!.PeakTokens.Should().Be(20_000);
    }

    [Fact]
    public void Inspect_ShouldTakeTheLargestShareOfTheCallsTurns_IncludingTheFinalShare()
    {
        var json = AnySession(Workspace, 1_000_000,
            Turn(CallStart.AddMinutes(1), 5, finalPercent: 12),
            Turn(CallStart.AddMinutes(2), 9));

        ReadingOf(json)!.PeakTokens.Should().Be(120_000);
    }

    [Fact]
    public void Inspect_WhenNoTurnEndedDuringTheCall_ShouldNotBeACandidate()
    {
        var json = AnySession(Workspace, 200_000, Turn(CallStart.AddMinutes(-5), 40));

        KiroSessionContext.Inspect(json, Workspace, CallStart).Should().BeNull();
    }

    [Fact]
    public void Inspect_WhenTheSessionBelongsToAnotherWorkspace_ShouldNotBeACandidate()
    {
        var json = AnySession("/home/ubuntu/work/other", 200_000, Turn(CallStart.AddMinutes(1), 21.5));

        KiroSessionContext.Inspect(json, Workspace, CallStart).Should().BeNull();
    }

    [Fact]
    public void Inspect_WhenTheWorkspaceDiffersOnlyByATrailingSlash_ShouldStillMatch()
    {
        var json = AnySession(Workspace + "/", 200_000, Turn(CallStart.AddMinutes(1), 50));

        ReadingOf(json)!.PeakTokens.Should().Be(100_000);
    }

    [Theory]
    [InlineData(null, 21.5)]     // no model info
    [InlineData(200_000L, null)] // the server sent no share
    public void Inspect_WhenTheShareOrTheWindowIsMissing_ShouldBeACandidateWithoutAReading(long? window, double? percent)
    {
        var json = AnySession(Workspace, window, Turn(CallStart.AddMinutes(1), percent));

        var candidate = KiroSessionContext.Inspect(json, Workspace, CallStart);

        candidate.Should().NotBeNull();
        candidate!.Context.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "cwd": "/home/ubuntu/work/repo" }""")]
    [InlineData("""{ "cwd": "/home/ubuntu/work/repo", "session_state": { "conversation_metadata": { "user_turn_metadatas": "x" } } }""")]
    public void Inspect_WhenTheFileHasAnotherShape_ShouldBeNullNotThrow(string json)
    {
        KiroSessionContext.Inspect(json, Workspace, CallStart).Should().BeNull();
    }

    [Fact]
    public void Inspect_WhenAShareIsNotAFiniteNumber_ShouldIgnoreItNotThrow()
    {
        var json = AnySession(Workspace, 200_000, Turn(CallStart.AddMinutes(1), 10))
            .Replace("\"context_usage_percentage\": 10", "\"context_usage_percentage\": 1e400", StringComparison.Ordinal);

        var act = () => KiroSessionContext.Inspect(json, Workspace, CallStart);

        act.Should().NotThrow();
        act()!.Context.Should().BeNull();
    }

    // ── Read: the call's session among the directory's files ────────────────

    [Fact]
    public void Read_WithOneSessionOfTheWorkspaceWrittenDuringTheCall_ShouldReportIt()
    {
        WriteSession("a", Session(Workspace, 200_000, "a", Turn(CallStart.AddMinutes(1), 30)));
        WriteSession("other", Session("/elsewhere", 200_000, "other", Turn(CallStart.AddMinutes(2), 90)));
        var old = WriteSession("old", Session(Workspace, 200_000, "old", Turn(CallStart.AddMinutes(2), 95)));
        File.SetLastWriteTimeUtc(old, CallStart.UtcDateTime.AddHours(-1));

        KiroSessionContext.Read(_dir, Workspace, CallStart, sessionId: null)
            .Should().Be(new KiroSessionContext.Reading(60_000, 200_000));
    }

    [Fact]
    public void Read_WithParallelSessionsAndNoKnownId_ShouldReportNothingRatherThanAnotherCallsContext()
    {
        // Parallel reviewers each record under their own phase; one must not get another's peak.
        WriteSession("a", Session(Workspace, 200_000, "a", Turn(CallStart.AddMinutes(1), 10)));
        WriteSession("b", Session(Workspace, 200_000, "b", Turn(CallStart.AddMinutes(2), 90)));

        KiroSessionContext.Read(_dir, Workspace, CallStart, sessionId: null).Should().BeNull();
    }

    [Fact]
    public void Read_WithParallelSessionsAndAKnownId_ShouldReportThatSession()
    {
        WriteSession("a", Session(Workspace, 200_000, "a", Turn(CallStart.AddMinutes(1), 10)));
        WriteSession("b", Session(Workspace, 200_000, "b", Turn(CallStart.AddMinutes(2), 90)));

        KiroSessionContext.Read(_dir, Workspace, CallStart, sessionId: "a")!.PeakTokens.Should().Be(20_000);
    }

    [Fact]
    public void Read_WhenTheOnlyCandidateHasNoShare_ShouldNotFallBackToAnotherSession()
    {
        WriteSession("a", Session(Workspace, 200_000, "a", Turn(CallStart.AddMinutes(1), null)));
        WriteSession("b", Session(Workspace, 200_000, "b", Turn(CallStart.AddMinutes(2), 90)));

        KiroSessionContext.Read(_dir, Workspace, CallStart, sessionId: "a").Should().BeNull();
    }

    [Fact]
    public void Read_WhenTheKnownIdIsNotAmongTheFiles_ShouldFallBackToTheOnlyCandidate()
    {
        WriteSession("a", Session(Workspace, 200_000, "a", Turn(CallStart.AddMinutes(1), 30)));

        KiroSessionContext.Read(_dir, Workspace, CallStart, sessionId: "listed-id-in-another-format")!
            .PeakTokens.Should().Be(60_000);
    }

    [Fact]
    public void Read_WhenTheDirectoryIsMissing_ShouldBeNull()
    {
        KiroSessionContext.Read(Path.Combine(_dir, "missing"), Workspace, CallStart, sessionId: null).Should().BeNull();
    }

    [Fact]
    public void SessionsDirectory_ShouldPreferKiroHome()
    {
        KiroSessionContext.SessionsDirectory("/data/kiro", "/home/ubuntu")
            .Should().Be(Path.Combine("/data/kiro", "sessions", "cli"));
        KiroSessionContext.SessionsDirectory(null, "/home/ubuntu")
            .Should().Be(Path.Combine("/home/ubuntu", ".kiro", "sessions", "cli"));
    }
}
