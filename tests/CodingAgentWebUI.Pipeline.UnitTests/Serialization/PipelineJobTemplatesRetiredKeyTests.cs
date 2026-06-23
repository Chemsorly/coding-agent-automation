using System.Text.Json;
using AwesomeAssertions;
using MessagePack;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Serialization;

/// <summary>
/// Backward-compatibility tests ensuring that removing the PipelineJobTemplates property
/// (Key 45) does not break deserialization of old persisted data.
/// </summary>
public class PipelineJobTemplatesRetiredKeyTests
{
    [Fact]
    public void Json_DeserializeWithPipelineJobTemplates_DoesNotThrow()
    {
        // Arrange: JSON from an old pipeline-config.json that still contains the field
        var json = """
        {
            "maxRetries": 3,
            "pipelineJobTemplates": [
                { "id": "t-1", "name": "Old Template", "issueProviderId": "ip-1", "repoProviderId": "rp-1", "enabled": true }
            ]
        }
        """;

        // Act & Assert: System.Text.Json ignores unknown properties by default
        var config = JsonSerializer.Deserialize<PipelineConfiguration>(json, PipelineJsonOptions.Default);
        config.Should().NotBeNull();
        config!.MaxRetries.Should().Be(3);
    }

    [Fact]
    public void Json_DeserializeWithEmptyPipelineJobTemplates_DoesNotThrow()
    {
        var json = """{ "pipelineJobTemplates": [] }""";

        var config = JsonSerializer.Deserialize<PipelineConfiguration>(json, PipelineJsonOptions.Default);
        config.Should().NotBeNull();
    }

    [Fact]
    public void MessagePack_DeserializeWithRetiredKey45_DoesNotThrow()
    {
        // Arrange: Construct a raw MessagePack payload that includes Key 45 with an
        // empty array, simulating data serialized by the old schema.
        var options = MessagePackSerializerOptions.Standard;

        // Serialize a current config to get a valid baseline payload
        var config = new PipelineConfiguration { MaxRetries = 5 };
        var baseBytes = MessagePackSerializer.Serialize(config, options);

        // MessagePackObject with int keys serializes as an array where index = key.
        // We need at least 46 elements (indices 0..45) to include Key 45.
        var reader = new MessagePackReader(baseBytes);
        var arrayLength = reader.ReadArrayHeader();

        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);

        var newLength = Math.Max(arrayLength, 46);
        writer.WriteArrayHeader(newLength);

        // Copy existing elements by tracking positions
        for (var i = 0; i < arrayLength; i++)
        {
            var before = reader.Consumed;
            reader.Skip();
            var after = reader.Consumed;
            writer.WriteRaw(baseBytes.AsSpan()[(int)before..(int)after]);
        }

        // Pad with nil for any missing indices between arrayLength and 44
        for (var i = arrayLength; i < 45; i++)
            writer.WriteNil();

        // Write Key 45 as an empty array (simulating old PipelineJobTemplates = [])
        writer.WriteArrayHeader(0);

        writer.Flush();
        var payload = bufferWriter.WrittenMemory.ToArray();

        // Act & Assert: Deserialization should succeed, ignoring the retired Key 45
        var deserialized = MessagePackSerializer.Deserialize<PipelineConfiguration>(payload, options);
        deserialized.Should().NotBeNull();
        deserialized!.MaxRetries.Should().Be(5);
    }
}
