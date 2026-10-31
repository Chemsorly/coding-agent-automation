using System.Buffers;
using System.Text.Json;
using AwesomeAssertions;
using MessagePack;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

public class IssueCommentVerificationWireTests
{
    // ─── MessagePack ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void MessagePack_RoundTrip_KeepsIsVerified(bool? isVerified)
    {
        var comment = new IssueComment
        {
            Author = "alice",
            Body = "hello",
            CreatedAt = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            Id = "42",
            IsVerified = isVerified
        };

        var bytes = MessagePackSerializer.Serialize(comment, AgentHubMessagePack.SerializerOptions);
        var deserialized = MessagePackSerializer.Deserialize<IssueComment>(bytes, AgentHubMessagePack.SerializerOptions);

        deserialized.IsVerified.Should().Be(isVerified);
        deserialized.Author.Should().Be("alice");
        deserialized.Body.Should().Be("hello");
        deserialized.Id.Should().Be("42");
    }

    [Fact]
    public void MessagePack_PayloadFromAnOlderApi_ReadsNull()
    {
        // Simulate an older API that only serialized keys 0-3 (no IsVerified at key 4).
        // Build a 4-element MessagePack array manually — no [MessagePackObject] needed,
        // which avoids the source-generator GeneratedMessagePackResolver conflict.
        var dt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dtBytes = MessagePackSerializer.Serialize(dt, AgentHubMessagePack.SerializerOptions);

        var bufferWriter = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);
        writer.WriteArrayHeader(4);   // Older API: only keys 0-3
        writer.Write("bob");          // Key 0: Author
        writer.Write("old message");  // Key 1: Body
        writer.WriteRaw(dtBytes);     // Key 2: CreatedAt (pre-serialized with same options)
        writer.Write("7");            // Key 3: Id
        writer.Flush();

        var deserialized = MessagePackSerializer.Deserialize<IssueComment>(
            bufferWriter.WrittenMemory, AgentHubMessagePack.SerializerOptions);

        deserialized.IsVerified.Should().BeNull();
        deserialized.Author.Should().Be("bob");
        deserialized.Body.Should().Be("old message");
        deserialized.Id.Should().Be("7");
    }

    // ─── JSON ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void Json_RoundTrip_KeepsIsVerified(bool? isVerified)
    {
        var comment = new IssueComment
        {
            Author = "alice",
            Body = "hello",
            CreatedAt = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            Id = "42",
            IsVerified = isVerified
        };

        var json = JsonSerializer.Serialize(comment, PipelineJsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<IssueComment>(json, PipelineJsonOptions.Default);

        deserialized!.IsVerified.Should().Be(isVerified);
        deserialized.Author.Should().Be("alice");
        deserialized.Body.Should().Be("hello");
        deserialized.Id.Should().Be("42");
    }

    [Fact]
    public void Json_PayloadFromAnOlderApi_ReadsNull()
    {
        const string json = """{"author":"a","body":"b","createdAt":"2026-01-01T00:00:00Z","id":"1"}""";

        var deserializedDefault = JsonSerializer.Deserialize<IssueComment>(json, PipelineJsonOptions.Default);
        deserializedDefault!.IsVerified.Should().BeNull();
        deserializedDefault.Author.Should().Be("a");
        deserializedDefault.Body.Should().Be("b");
        deserializedDefault.Id.Should().Be("1");

        var deserializedLenient = JsonSerializer.Deserialize<IssueComment>(json, PipelineJsonOptions.Lenient);
        deserializedLenient!.IsVerified.Should().BeNull();
        deserializedLenient.Author.Should().Be("a");
    }
}
