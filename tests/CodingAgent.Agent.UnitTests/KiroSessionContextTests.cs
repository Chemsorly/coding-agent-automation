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

    private static string Turn(DateTimeOffset end, double? percent, double? finalPercent = null) =>
        $$"""
        {
          "end_reason": "UserTurnEnd",
          "end_timestamp": "{{end.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffffZ}}",
          "input_token_count": 0,
          "output_token_count": 0,
          "context_usage_percentage": {{(percent is null ? "null" : percent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}},
          "final_context_usage_percentage": {{(finalPercent is null ? "null" : finalPercent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}},
          "metering_usage": []
        }
        """;

    private static string Session(string cwd, long? window, params string[] turns) =>
        $$"""
        {
          "session_id": "aaaaaaaa-0000-0000-0000-000000000001",
          "cwd": "{{cwd}}",
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

    private string WriteSession(string name, string json)
    {
        var path = Path.Combine(_dir, name + ".json");
        File.WriteAllText(path, json);
        File.SetLastWriteTimeUtc(path, CallStart.UtcDateTime.AddMinutes(5));
        return path;
    }

    // ── Parse ───────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_WhenATurnOfTheCallReportsItsShare_ShouldTurnItIntoTokensOfTheWindow()
    {
        var json = Session(Workspace, 200_000, Turn(CallStart.AddMinutes(1), 21.5));

        var context = KiroSessionContext.Parse(json, Workspace, CallStart);

        context.Should().Be(new KiroSessionContext.Reading(43_000, 200_000));
    }

    [Fact]
    public void Parse_ShouldIgnoreTurnsThatEndedBeforeTheCall()
    {
        // A resumed session keeps the turns of earlier phases.
        var json = Session(Workspace, 200_000,
            Turn(CallStart.AddMinutes(-30), 80),
            Turn(CallStart.AddMinutes(2), 10));

        KiroSessionContext.Parse(json, Workspace, CallStart)!.PeakTokens.Should().Be(20_000);
    }

    [Fact]
    public void Parse_ShouldTakeTheLargestShareOfTheCallsTurns_IncludingTheFinalShare()
    {
        var json = Session(Workspace, 1_000_000,
            Turn(CallStart.AddMinutes(1), 5, finalPercent: 12),
            Turn(CallStart.AddMinutes(2), 9));

        KiroSessionContext.Parse(json, Workspace, CallStart)!.PeakTokens.Should().Be(120_000);
    }

    [Fact]
    public void Parse_WhenTheSessionBelongsToAnotherWorkspace_ShouldBeNull()
    {
        var json = Session("/home/ubuntu/work/other", 200_000, Turn(CallStart.AddMinutes(1), 21.5));

        KiroSessionContext.Parse(json, Workspace, CallStart).Should().BeNull();
    }

    [Fact]
    public void Parse_WhenTheWorkspaceDiffersOnlyByATrailingSlash_ShouldStillMatch()
    {
        var json = Session(Workspace + "/", 200_000, Turn(CallStart.AddMinutes(1), 50));

        KiroSessionContext.Parse(json, Workspace, CallStart)!.PeakTokens.Should().Be(100_000);
    }

    [Theory]
    [InlineData(null, 21.5)]   // no model info
    [InlineData(200_000L, null)] // the server sent no share
    public void Parse_WhenTheShareOrTheWindowIsMissing_ShouldBeNull(long? window, double? percent)
    {
        var json = Session(Workspace, window, Turn(CallStart.AddMinutes(1), percent));

        KiroSessionContext.Parse(json, Workspace, CallStart).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "cwd": "/home/ubuntu/work/repo" }""")]
    [InlineData("""{ "cwd": "/home/ubuntu/work/repo", "session_state": { "conversation_metadata": { "user_turn_metadatas": "x" } } }""")]
    public void Parse_WhenTheFileHasAnotherShape_ShouldBeNullNotThrow(string json)
    {
        KiroSessionContext.Parse(json, Workspace, CallStart).Should().BeNull();
    }

    // ── Read (directory) ────────────────────────────────────────────────────

    [Fact]
    public void Read_ShouldTakeTheFullestSessionOfTheWorkspaceWrittenDuringTheCall()
    {
        WriteSession("a", Session(Workspace, 200_000, Turn(CallStart.AddMinutes(1), 10)));
        WriteSession("b", Session(Workspace, 200_000, Turn(CallStart.AddMinutes(2), 30)));
        WriteSession("other", Session("/elsewhere", 200_000, Turn(CallStart.AddMinutes(2), 90)));
        var old = WriteSession("old", Session(Workspace, 200_000, Turn(CallStart.AddMinutes(2), 95)));
        File.SetLastWriteTimeUtc(old, CallStart.UtcDateTime.AddHours(-1));

        var context = KiroSessionContext.Read(_dir, Workspace, CallStart);

        context.Should().Be(new KiroSessionContext.Reading(60_000, 200_000));
    }

    [Fact]
    public void Read_WhenTheDirectoryIsMissing_ShouldBeNull()
    {
        KiroSessionContext.Read(Path.Combine(_dir, "missing"), Workspace, CallStart).Should().BeNull();
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
