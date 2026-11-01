using System.Buffers;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using MessagePack;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Wire-contract tests for <see cref="IssueComment.IsVerified"/>: verifies that the new nullable bool
/// field round-trips correctly over both MessagePack and JSON, and that payloads from an older API
/// (missing key 4 or missing isVerified) deserialize to null rather than false.
/// </summary>
public class IssueCommentVerificationWireTests
{
    // ─── MessagePack round-trip ────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void MessagePack_RoundTrip_KeepsIsVerified(bool? value)
    {
        var comment = new IssueComment
        {
            Author = "alice",
            Body = "hello",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Id = "1",
            IsVerified = value
        };

        var bytes = MessagePackSerializer.Serialize(comment, AgentHubMessagePack.SerializerOptions);
        var deserialized = MessagePackSerializer.Deserialize<IssueComment>(bytes, AgentHubMessagePack.SerializerOptions);

        deserialized.IsVerified.Should().Be(value);
        deserialized.Author.Should().Be("alice");
        deserialized.Body.Should().Be("hello");
        deserialized.Id.Should().Be("1");
    }

    // ─── MessagePack backward compatibility: older API omits key 4 ────────────

    [Fact]
    public void MessagePack_PayloadFromAnOlderApi_ReadsNull()
    {
        // Build a 4-element MessagePack array (keys 0-3 only) to simulate an older API payload.
        // Key 4 (IsVerified) is absent — must deserialize as null, not false.
        var bufferWriter = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);
        writer.WriteArrayHeader(4);
        writer.Write("bob");          // key 0: Author
        writer.Write("old comment");  // key 1: Body
        // key 2: CreatedAt — use the same serializer so the DateTime extension type matches
        var dt = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        MessagePackSerializer.Serialize(ref writer, dt, AgentHubMessagePack.SerializerOptions);
        writer.Write("42");           // key 3: Id
        writer.Flush();
        var bytes = bufferWriter.WrittenSpan.ToArray();

        var deserialized = MessagePackSerializer.Deserialize<IssueComment>(bytes, AgentHubMessagePack.SerializerOptions);

        deserialized.IsVerified.Should().BeNull("an older API payload without key 4 must not default to false");
        deserialized.Author.Should().Be("bob");
        deserialized.Body.Should().Be("old comment");
        deserialized.Id.Should().Be("42");
    }

    // ─── JSON round-trip ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void Json_RoundTrip_KeepsIsVerified(bool? value)
    {
        var comment = new IssueComment
        {
            Author = "carol",
            Body = "json test",
            CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Id = "99",
            IsVerified = value
        };

        var json = JsonSerializer.Serialize(comment, PipelineJsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<IssueComment>(json, PipelineJsonOptions.Default);

        deserialized.Should().NotBeNull();
        deserialized!.IsVerified.Should().Be(value);
        deserialized.Author.Should().Be("carol");
        deserialized.Id.Should().Be("99");
    }

    // ─── JSON backward compatibility: older API omits isVerified ─────────────

    [Fact]
    public void Json_PayloadFromAnOlderApi_ReadsNull()
    {
        const string json = """{"author":"a","body":"b","createdAt":"2026-01-01T00:00:00Z","id":"1"}""";

        var resultDefault = JsonSerializer.Deserialize<IssueComment>(json, PipelineJsonOptions.Default);
        var resultLenient = JsonSerializer.Deserialize<IssueComment>(json, PipelineJsonOptions.Lenient);

        resultDefault.Should().NotBeNull();
        resultDefault!.IsVerified.Should().BeNull("Default options: missing isVerified must deserialize to null");
        resultDefault.Author.Should().Be("a");

        resultLenient.Should().NotBeNull();
        resultLenient!.IsVerified.Should().BeNull("Lenient options: missing isVerified must deserialize to null");
        resultLenient.Author.Should().Be("a");
    }
}
