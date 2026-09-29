using System.Text.Json;
using AwesomeAssertions;
using MessagePack;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Serialization;

/// <summary>
/// Backward compatibility of <see cref="CodeReviewConfiguration"/> with payloads written before
/// ReviewIsolation was removed: its JSON property is ignored, and MessagePack Keys 3 and 4 are retired.
/// </summary>
public class CodeReviewRetiredKeyTests
{
    [Theory]
    [InlineData("\"Shared\"")]
    [InlineData("\"Isolated\"")]
    [InlineData("1")]
    public void Json_StoredReviewIsolation_IsIgnored(string storedValue)
    {
        var json = $$"""
        {
            "maxIterations": 4,
            "reviewIsolation": {{storedValue}},
            "fixPrompt": "Fix it"
        }
        """;

        var config = JsonSerializer.Deserialize<CodeReviewConfiguration>(json, PipelineJsonOptions.Lenient);

        config.Should().NotBeNull();
        config!.MaxIterations.Should().Be(4);
        config.FixPrompt.Should().Be("Fix it");
    }

    [Fact]
    public void MessagePack_PayloadWithRetiredKeys3And4_Deserializes()
    {
        // Old payloads carry a stale value at Key(3) and the former ReviewIsolation at Key(4).
        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);

        writer.WriteArrayHeader(5);
        writer.Write("Fix"); // Key(0): FixPrompt
        writer.WriteNil();   // Key(1): InlineComments = nil (uses default)
        writer.Write(3);     // Key(2): MaxIterations
        writer.Write(0);     // Key(3): retired
        writer.Write(1);     // Key(4): retired (was ReviewIsolation; 1 was the removed Shared value)
        writer.Flush();

        var deserialized = MessagePackSerializer.Deserialize<CodeReviewConfiguration>(
            bufferWriter.WrittenMemory.ToArray(), MessagePackSerializerOptions.Standard);

        deserialized.Should().NotBeNull();
        deserialized!.FixPrompt.Should().Be("Fix");
        deserialized.MaxIterations.Should().Be(3);
    }

    [Fact]
    public void MessagePack_RoundTrip_PreservesValues()
    {
        var config = new CodeReviewConfiguration { FixPrompt = "Fix", MaxIterations = 4 };

        var bytes = MessagePackSerializer.Serialize(config, MessagePackSerializerOptions.Standard);
        var deserialized = MessagePackSerializer.Deserialize<CodeReviewConfiguration>(bytes, MessagePackSerializerOptions.Standard);

        deserialized.Should().BeEquivalentTo(config);
    }
}
