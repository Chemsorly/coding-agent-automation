using AwesomeAssertions;
using CodingAgent.Agent.KiroCli;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="KiroCliAgentProvider.ParseLatestSessionId"/>, which reads
/// <c>kiro-cli chat --list-sessions --format json</c> output.
/// </summary>
public class KiroCliSessionListParsingTests
{
    [Fact]
    public void ParseLatestSessionId_Kiro2_29Output_ReturnsTheFirstListedSession()
    {
        const string json =
            """[{"cwd":"/w","sessions":[{"sessionId":"newest","source":"v2","updatedAt":"2026-10-10T15:06:53.016Z","messageCount":4},{"sessionId":"older","source":"v2","updatedAt":"2026-10-10T15:06:01.371Z","messageCount":8}],"complete":true}]""";

        KiroCliAgentProvider.ParseLatestSessionId(json).Should().Be("newest");
    }

    [Fact]
    public void ParseLatestSessionId_Kiro2_10Output_WithoutComplete_ReturnsTheFirstListedSession()
    {
        const string json =
            """[{"cwd":"/w","sessions":[{"sessionId":"newest","source":"v2","updatedAt":"2026-10-10T15:06:53.016Z"},{"sessionId":"older","updatedAt":"2026-10-10T15:06:01.371Z"}]}]""";

        KiroCliAgentProvider.ParseLatestSessionId(json).Should().Be("newest");
    }

    [Fact]
    public void ParseLatestSessionId_LaterEntryWithNewerUpdatedAt_Wins()
    {
        const string json =
            """[{"cwd":"/w","sessions":[{"sessionId":"older","updatedAt":"2026-10-10T15:06:01Z"},{"sessionId":"newest","updatedAt":"2026-10-10T15:07:00Z"}]}]""";

        KiroCliAgentProvider.ParseLatestSessionId(json).Should().Be("newest");
    }

    [Fact]
    public void ParseLatestSessionId_NoUpdatedAt_ReturnsTheFirstListedSession()
    {
        const string json = """[{"cwd":"/w","sessions":[{"sessionId":"first"},{"sessionId":"second"}]}]""";

        KiroCliAgentProvider.ParseLatestSessionId(json).Should().Be("first");
    }

    [Fact]
    public void ParseLatestSessionId_EntryWithoutSessionId_IsSkipped()
    {
        const string json = """[{"cwd":"/w","sessions":[{"title":"no id"},{"sessionId":""},{"sessionId":"real"}]}]""";

        KiroCliAgentProvider.ParseLatestSessionId(json).Should().Be("real");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("""[{"cwd":"/w","sessions":[],"complete":true}]""")]
    [InlineData("""{"sessions":[{"sessionId":"x"}]}""")]
    [InlineData("Chat sessions for /w:\nChat SessionId: 5cd7489d-9219-4ae4-a304-f8ff78d689de")]
    [InlineData("""[{"cwd":"/w","sessions":[{"sessionId":"x" """)]
    public void ParseLatestSessionId_NoSessionOrNotTheJsonList_ReturnsNull(string output)
    {
        KiroCliAgentProvider.ParseLatestSessionId(output).Should().BeNull();
    }
}
