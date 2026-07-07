using System.Text.Json;
using AwesomeAssertions;
using MessagePack;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Serialization;

/// <summary>
/// Backward-compatibility tests ensuring that removing the ReviewIsolation property
/// (Key 3) from CodeReviewConfiguration does not break deserialization of old persisted data.
/// </summary>
public class CodeReviewIsolationRetiredKeyTests
{
    [Fact]
    public void Json_DeserializeWithReviewIsolationShared_DoesNotThrow()
    {
        // Arrange: JSON from an old config that still contains ReviewIsolation
        var json = """
        {
            "MaxIterations": 3,
            "ReviewIsolation": "Shared"
        }
        """;

        // Act & Assert: System.Text.Json ignores unknown properties by default
        var config = JsonSerializer.Deserialize<CodeReviewConfiguration>(json);
        config.Should().NotBeNull();
        config!.MaxIterations.Should().Be(3);
    }

    [Fact]
    public void Json_DeserializeWithReviewIsolationIsolated_DoesNotThrow()
    {
        var json = """
        {
            "MaxIterations": 2,
            "ReviewIsolation": "Isolated"
        }
        """;

        var config = JsonSerializer.Deserialize<CodeReviewConfiguration>(json);
        config.Should().NotBeNull();
        config!.MaxIterations.Should().Be(2);
    }

    [Fact]
    public void Json_DeserializeWithReviewIsolation_ViaLenientOptions_DoesNotThrow()
    {
        // Config import uses lenient options with case-insensitive matching
        var json = """
        {
            "maxIterations": 4,
            "reviewIsolation": "Shared",
            "fixPrompt": "Fix it"
        }
        """;

        var config = JsonSerializer.Deserialize<CodeReviewConfiguration>(json, PipelineJsonOptions.Lenient);
        config.Should().NotBeNull();
        config!.MaxIterations.Should().Be(4);
        config.FixPrompt.Should().Be("Fix it");
    }

    [Fact]
    public void MessagePack_DeserializeWithRetiredKey3_DoesNotThrow()
    {
        // Arrange: Construct a raw MessagePack payload that includes Key 3 with an
        // integer value (old enum: 0=Shared, 1=Isolated), simulating data serialized
        // by the old schema.
        var options = MessagePackSerializerOptions.Standard;

        // Serialize a current config to get a valid baseline payload
        var config = new CodeReviewConfiguration { MaxIterations = 5 };
        var baseBytes = MessagePackSerializer.Serialize(config, options);

        // MessagePackObject with int keys serializes as an array where index = key.
        // Old schema had keys 0-3. Current schema has keys 0-2.
        // We need 4 elements (indices 0..3) to include the retired Key 3.
        var reader = new MessagePackReader(baseBytes);
        var arrayLength = reader.ReadArrayHeader();

        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);

        // Write an array with 4 elements (to include the retired Key 3)
        writer.WriteArrayHeader(4);

        // Copy existing elements (keys 0-2)
        for (var i = 0; i < arrayLength; i++)
        {
            var before = reader.Consumed;
            reader.Skip();
            var after = reader.Consumed;
            writer.WriteRaw(baseBytes.AsSpan()[(int)before..(int)after]);
        }

        // Pad with nil for any missing indices between arrayLength and 2
        for (var i = arrayLength; i < 3; i++)
            writer.WriteNil();

        // Write Key 3 as integer 0 (simulating old ReviewIsolation.Shared enum value)
        writer.Write(0);

        writer.Flush();
        var payload = bufferWriter.WrittenMemory.ToArray();

        // Act & Assert: Deserialization should succeed, ignoring the retired Key 3
        var deserialized = MessagePackSerializer.Deserialize<CodeReviewConfiguration>(payload, options);
        deserialized.Should().NotBeNull();
        deserialized!.MaxIterations.Should().Be(5);
    }

    [Fact]
    public void MessagePack_DeserializeWithRetiredKey3_IsolatedValue_DoesNotThrow()
    {
        // Same as above but with enum value 1 (Isolated)
        var options = MessagePackSerializerOptions.Standard;

        var config = new CodeReviewConfiguration { MaxIterations = 2, FixPrompt = "Fix" };
        var baseBytes = MessagePackSerializer.Serialize(config, options);

        var reader = new MessagePackReader(baseBytes);
        var arrayLength = reader.ReadArrayHeader();

        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);

        writer.WriteArrayHeader(4);

        for (var i = 0; i < arrayLength; i++)
        {
            var before = reader.Consumed;
            reader.Skip();
            var after = reader.Consumed;
            writer.WriteRaw(baseBytes.AsSpan()[(int)before..(int)after]);
        }

        for (var i = arrayLength; i < 3; i++)
            writer.WriteNil();

        // Write Key 3 as integer 1 (simulating old ReviewIsolation.Isolated enum value)
        writer.Write(1);

        writer.Flush();
        var payload = bufferWriter.WrittenMemory.ToArray();

        var deserialized = MessagePackSerializer.Deserialize<CodeReviewConfiguration>(payload, options);
        deserialized.Should().NotBeNull();
        deserialized!.MaxIterations.Should().Be(2);
        deserialized.FixPrompt.Should().Be("Fix");
    }
}
