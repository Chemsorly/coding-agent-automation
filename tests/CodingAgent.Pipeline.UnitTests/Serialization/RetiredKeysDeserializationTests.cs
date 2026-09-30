using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using MessagePack;
using MessagePack.Resolvers;

namespace CodingAgent.Pipeline.UnitTests.Serialization;

/// <summary>
/// Backward-compatibility tests ensuring that removing 7 dead settings from
/// <see cref="PipelineConfiguration"/> (issue #3149) does not break deserialization
/// of existing persisted data that still contains those fields.
/// </summary>
public class RetiredKeysDeserializationTests
{
    private static readonly MessagePackSerializerOptions MsgPackOptions =
        ContractlessStandardResolverAllowPrivate.Options;

    // ── JSON backward-compatibility ─────────────────────────────────────────────

    /// <summary>
    /// Acceptance criterion: A stored pipeline configuration that still contains the
    /// removed fields (agentDisconnectGracePeriod, agentBusyProgressTimeout,
    /// heartbeatSweepIntervalSeconds, heartbeatTimeoutSeconds, issuePageSize,
    /// lastUsedProviderIds, maxConsolidationDispatchRetries) loads without error.
    /// System.Text.Json ignores unknown properties by default.
    /// </summary>
    [Fact]
    public void Json_DeserializeWithAllRetiredFields_DoesNotThrow()
    {
        // Arrange: JSON payload from an old pipeline-config.json that still contains
        // all 7 fields removed in #3149.
        var json = """
        {
            "maxRetries": 3,
            "agentDisconnectGracePeriod": "00:05:00",
            "agentBusyProgressTimeout": "01:00:00",
            "heartbeatSweepIntervalSeconds": 60,
            "heartbeatTimeoutSeconds": 90,
            "issuePageSize": 25,
            "lastUsedProviderIds": {
                "issue": "ip-1",
                "repository": "rp-1",
                "agent": "ap-1"
            },
            "maxConsolidationDispatchRetries": 5
        }
        """;

        // Act & Assert: System.Text.Json ignores unknown properties (retired fields)
        var config = JsonSerializer.Deserialize<PipelineConfiguration>(json, PipelineJsonOptions.Default);
        config.Should().NotBeNull();
        // Verify the non-retired field was preserved
        config!.MaxRetries.Should().Be(3);
    }

    [Fact]
    public void Json_DeserializeWithRetiredAgentHealthFields_DoesNotThrow()
    {
        // Arrange: specifically the heartbeat and agent timing fields
        var json = """
        {
            "maxRetries": 5,
            "agentDisconnectGracePeriod": "00:10:00",
            "agentBusyProgressTimeout": "01:30:00",
            "heartbeatSweepIntervalSeconds": 30,
            "heartbeatTimeoutSeconds": 60
        }
        """;

        var config = JsonSerializer.Deserialize<PipelineConfiguration>(json, PipelineJsonOptions.Default);
        config.Should().NotBeNull();
        config!.MaxRetries.Should().Be(5);
    }

    [Fact]
    public void Json_DeserializeWithRetiredIssuePageSizeAndLastUsedProviderIds_DoesNotThrow()
    {
        var json = """
        {
            "maxRetries": 2,
            "issuePageSize": 50,
            "lastUsedProviderIds": { "issue": "ip-x" }
        }
        """;

        var config = JsonSerializer.Deserialize<PipelineConfiguration>(json, PipelineJsonOptions.Default);
        config.Should().NotBeNull();
        config!.MaxRetries.Should().Be(2);
    }

    [Fact]
    public void Json_DeserializeWithRetiredMaxConsolidationDispatchRetries_DoesNotThrow()
    {
        var json = """
        {
            "maxRetries": 7,
            "maxConsolidationDispatchRetries": 10
        }
        """;

        var config = JsonSerializer.Deserialize<PipelineConfiguration>(json, PipelineJsonOptions.Default);
        config.Should().NotBeNull();
        config!.MaxRetries.Should().Be(7);
    }

    // ── PipelineProject JSON backward-compatibility ─────────────────────────────

    /// <summary>
    /// Acceptance criterion: A stored project that still contains the removed
    /// maxConsolidationDispatchRetries field loads without error.
    /// </summary>
    [Fact]
    public void Json_PipelineProject_DeserializeWithRetiredMaxConsolidationDispatchRetries_DoesNotThrow()
    {
        // Arrange: JSON from an old project config that still contains the field
        var json = """
        {
            "id": "proj-1",
            "name": "My Project",
            "enabled": true,
            "maxConsolidationDispatchRetries": 7
        }
        """;

        // Act & Assert: System.Text.Json ignores the unknown field
        var project = JsonSerializer.Deserialize<PipelineProject>(json, PipelineJsonOptions.Default);
        project.Should().NotBeNull();
        project!.Name.Should().Be("My Project");
        project.Enabled.Should().BeTrue();
    }

    // ── MessagePack backward-compatibility ─────────────────────────────────────

    /// <summary>
    /// Acceptance criterion: A MessagePack payload that includes the retired Keys
    /// (2=AgentBusyProgressTimeout, 3=AgentDisconnectGracePeriod,
    ///  29=HeartbeatSweepIntervalSeconds, 30=HeartbeatTimeoutSeconds,
    ///  33=IssuePageSize, 34=LastUsedProviderIds, 74=MaxConsolidationDispatchRetries)
    /// deserializes without error, and all non-retired fields are preserved.
    /// </summary>
    [Fact]
    public void MessagePack_DeserializeWithRetiredKeys_DoesNotThrow()
    {
        // Arrange: serialize a known config to get a valid baseline byte array
        var config = new PipelineConfiguration { MaxRetries = 5 };
        var baseBytes = MessagePackSerializer.Serialize(config, MsgPackOptions);

        // Build a payload that includes extra keys for the retired indices.
        // MessagePackObject with int keys serializes as an array where index = key.
        // We need to extend the array to at least index 75 to cover Key(74).
        var reader = new MessagePackReader(baseBytes);
        var arrayLength = reader.ReadArrayHeader();

        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);

        // Extend array to cover all retired key indices (max is 74)
        var newLength = Math.Max(arrayLength, 75);
        writer.WriteArrayHeader(newLength);

        // Copy existing elements
        for (var i = 0; i < arrayLength; i++)
        {
            var before = reader.Consumed;
            reader.Skip();
            var after = reader.Consumed;
            writer.WriteRaw(baseBytes.AsSpan()[(int)before..(int)after]);
        }

        // Pad missing indices with nil up to newLength - 1 (filling in retired key slots)
        // TODO: This pads retired scalar key slots (2=TimeSpan, 3=TimeSpan, 29=int, 30=int,
        // 33=int, 74=int) with nil rather than with correctly-typed old values. A payload
        // with an actual TimeSpan/int at those positions could behave differently. Consider
        // extending this test to write typed values (e.g. writer.Write(60) for Key(29))
        // to fully cover the "real old data" backward-compat scenario.
        for (var i = arrayLength; i < newLength; i++)
            writer.WriteNil();

        writer.Flush();
        var payload = bufferWriter.WrittenMemory.ToArray();

        // Act & Assert: deserialize should succeed, ignoring all retired key slots
        var deserialized = MessagePackSerializer.Deserialize<PipelineConfiguration>(payload, MsgPackOptions);
        deserialized.Should().NotBeNull();
        // MaxRetries is Key(0) — should be preserved
        deserialized!.MaxRetries.Should().Be(5);
    }

    [Fact]
    public void MessagePack_DeserializeWithRetiredKey33And34_DoesNotThrow()
    {
        // Arrange: a config with Key(33)=IssuePageSize and Key(34)=LastUsedProviderIds
        // set to well-formed non-nil values (simulating real old serialized data).
        var config = new PipelineConfiguration { MaxRetries = 3 };
        var baseBytes = MessagePackSerializer.Serialize(config, MsgPackOptions);

        var reader = new MessagePackReader(baseBytes);
        var arrayLength = reader.ReadArrayHeader();

        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);

        // Need at least 35 elements for Key(34)
        var newLength = Math.Max(arrayLength, 35);
        writer.WriteArrayHeader(newLength);

        for (var i = 0; i < arrayLength; i++)
        {
            var before = reader.Consumed;
            reader.Skip();
            var after = reader.Consumed;
            writer.WriteRaw(baseBytes.AsSpan()[(int)before..(int)after]);
        }

        for (var i = arrayLength; i < newLength; i++)
        {
            if (i == 33)
            {
                // old IssuePageSize = 25 (int)
                writer.Write(25);
            }
            else if (i == 34)
            {
                // old LastUsedProviderIds = {"issue": "ip-1"} (Dictionary<string, string>)
                // Must write a complete, well-formed map: header + key string + value string
                writer.WriteMapHeader(1);
                writer.Write("issue");
                writer.Write("ip-1");
            }
            else
            {
                writer.WriteNil();
            }
        }

        writer.Flush();
        var payload = bufferWriter.WrittenMemory.ToArray();

        // Act & Assert: deserialize must succeed without exception.
        // The ContractlessStandardResolverAllowPrivate resolver skips array slots
        // that correspond to no active [Key] on the current type (retired keys 33 and 34),
        // so a well-formed payload with those slots present loads without error.
        var deserialized = MessagePackSerializer.Deserialize<PipelineConfiguration>(payload, MsgPackOptions);
        deserialized.Should().NotBeNull();
        // Key(0)=MaxRetries must be preserved
        deserialized!.MaxRetries.Should().Be(3);
    }
}
