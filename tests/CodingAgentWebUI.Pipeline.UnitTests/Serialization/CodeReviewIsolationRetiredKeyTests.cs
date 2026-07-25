using System.Text.Json;
using AwesomeAssertions;
using MessagePack;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Serialization;

/// <summary>
/// Backward-compatibility tests for retired Key(3) in CodeReviewConfiguration.
/// Key(3) was previously used, retired, then briefly reused for ReviewIsolation (now at Key(4)).
/// These tests verify that stale values at index 3 in old MessagePack payloads do NOT
/// silently set ReviewIsolation to Shared — it must remain at its default (Isolated).
/// </summary>
public class CodeReviewIsolationRetiredKeyTests
{
    // TODO: Json_DeserializeWithReviewIsolationShared_DoesNotThrow, Json_DeserializeWithReviewIsolationIsolated_DoesNotThrow,
    // and Json_DeserializeWithReviewIsolation_ViaLenientOptions_DoesNotThrow do not assert the deserialized
    // ReviewIsolation property value. If the enum-to-string mapping broke, these tests would still pass.
    // Add assertions like: config.ReviewIsolation.Should().Be(ReviewIsolation.Shared) to each.
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

        // Act & Assert: System.Text.Json deserializes ReviewIsolation by property name
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
        // integer value (old enum: 0=Shared), simulating data serialized by the old schema.
        // After the fix (ReviewIsolation moved to Key(4)), Key(3) is ignored and
        // ReviewIsolation defaults to Isolated.
        var options = MessagePackSerializerOptions.Standard;

        // Manually construct a 4-element array: [nil, nil, 5, 0]
        // Key(0)=nil (FixPrompt), Key(1)=nil (InlineComments), Key(2)=5 (MaxIterations), Key(3)=0 (stale)
        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);

        writer.WriteArrayHeader(4);
        writer.WriteNil();  // Key(0): FixPrompt = null
        writer.WriteNil();  // Key(1): InlineComments = nil (uses default)
        writer.Write(5);    // Key(2): MaxIterations = 5
        writer.Write(0);    // Key(3): stale value (was ReviewIsolation.Shared in old schema)

        writer.Flush();
        var payload = bufferWriter.WrittenMemory.ToArray();

        // Act
        var deserialized = MessagePackSerializer.Deserialize<CodeReviewConfiguration>(payload, options);

        // Assert: Key(3) is retired — stale value must NOT set ReviewIsolation to Shared
        deserialized.Should().NotBeNull();
        deserialized!.MaxIterations.Should().Be(5);
        deserialized.ReviewIsolation.Should().Be(ReviewIsolation.Isolated);
    }

    [Fact]
    public void MessagePack_DeserializeWithRetiredKey3_IsolatedValue_DoesNotThrow()
    {
        // Same as above but with enum value 1 (Isolated) at Key(3) — must still be ignored.
        var options = MessagePackSerializerOptions.Standard;

        // Manually construct a 4-element array: ["Fix", nil, 2, 1]
        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);

        writer.WriteArrayHeader(4);
        writer.Write("Fix"); // Key(0): FixPrompt = "Fix"
        writer.WriteNil();   // Key(1): InlineComments = nil (uses default)
        writer.Write(2);     // Key(2): MaxIterations = 2
        writer.Write(1);     // Key(3): stale value (was ReviewIsolation.Isolated in old schema)

        writer.Flush();
        var payload = bufferWriter.WrittenMemory.ToArray();

        // Act
        var deserialized = MessagePackSerializer.Deserialize<CodeReviewConfiguration>(payload, options);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.MaxIterations.Should().Be(2);
        deserialized.FixPrompt.Should().Be("Fix");
        deserialized.ReviewIsolation.Should().Be(ReviewIsolation.Isolated);
    }

    // TODO: This test is functionally identical to MessagePack_DeserializeWithRetiredKey3_DoesNotThrow
    // (both construct a 4-element array with Key(3)=0 and assert ReviewIsolation == Isolated).
    // Consider consolidating to reduce maintenance cost.
    [Fact]
    public void MessagePack_DeserializeOldPayloadWithKey3Zero_DoesNotSetReviewIsolationToShared()
    {
        // Acceptance criterion #2: A payload serialized with the old Key(3) value of 0
        // does NOT set ReviewIsolation to Shared.
        var options = MessagePackSerializerOptions.Standard;

        // Construct old-schema payload: 4 elements with Key(3)=0 (Shared enum value)
        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);

        writer.WriteArrayHeader(4);
        writer.WriteNil();  // Key(0): FixPrompt = null
        writer.WriteNil();  // Key(1): InlineComments = nil
        writer.Write(2);    // Key(2): MaxIterations = 2 (default)
        writer.Write(0);    // Key(3): retired — old ReviewIsolation.Shared value

        writer.Flush();
        var payload = bufferWriter.WrittenMemory.ToArray();

        // Act
        var deserialized = MessagePackSerializer.Deserialize<CodeReviewConfiguration>(payload, options);

        // Assert: ReviewIsolation must NOT be Shared — must be the default (Isolated)
        deserialized.Should().NotBeNull();
        deserialized!.ReviewIsolation.Should().NotBe(ReviewIsolation.Shared);
        deserialized.ReviewIsolation.Should().Be(ReviewIsolation.Isolated);
    }

    [Fact]
    public void MessagePack_RoundTrip_NewPayload_DefaultsToIsolated()
    {
        // Acceptance criterion #3: Default value of ReviewIsolation remains Isolated
        // for newly serialized payloads.
        var options = MessagePackSerializerOptions.Standard;

        // Serialize a default CodeReviewConfiguration
        var config = new CodeReviewConfiguration();
        var bytes = MessagePackSerializer.Serialize(config, options);

        // Deserialize
        var deserialized = MessagePackSerializer.Deserialize<CodeReviewConfiguration>(bytes, options);

        // Assert: ReviewIsolation defaults to Isolated
        deserialized.Should().NotBeNull();
        deserialized!.ReviewIsolation.Should().Be(ReviewIsolation.Isolated);
    }

    [Fact]
    public void MessagePack_RoundTrip_ExplicitShared_PreservesValue()
    {
        // Verify that explicitly setting ReviewIsolation=Shared still round-trips correctly
        // with the new Key(4) assignment.
        var options = MessagePackSerializerOptions.Standard;

        var config = new CodeReviewConfiguration { ReviewIsolation = ReviewIsolation.Shared };
        var bytes = MessagePackSerializer.Serialize(config, options);
        var deserialized = MessagePackSerializer.Deserialize<CodeReviewConfiguration>(bytes, options);

        deserialized.Should().NotBeNull();
        deserialized!.ReviewIsolation.Should().Be(ReviewIsolation.Shared);
    }

    [Fact]
    public void MessagePack_OldPayloadWithStaleKey3_AndNewKey4_UsesKey4()
    {
        // Edge case: a payload that has BOTH Key(3)=0 (stale) and Key(4)=0 (Isolated)
        // — for example, during rolling deployment when an old orchestrator adds Key(3)
        // and a new orchestrator adds Key(4). The new schema reads Key(4).
        var options = MessagePackSerializerOptions.Standard;

        // Construct 5-element array: [nil, nil, 2, 0, 0]
        // Key(3)=0 (stale, ignored), Key(4)=0 (Isolated in new enum)
        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);

        writer.WriteArrayHeader(5);
        writer.WriteNil();  // Key(0): FixPrompt = null
        writer.WriteNil();  // Key(1): InlineComments = nil
        writer.Write(2);    // Key(2): MaxIterations = 2
        writer.Write(0);    // Key(3): stale (ignored)
        writer.Write(0);    // Key(4): ReviewIsolation = Isolated (enum value 0)

        writer.Flush();
        var payload = bufferWriter.WrittenMemory.ToArray();

        var deserialized = MessagePackSerializer.Deserialize<CodeReviewConfiguration>(payload, options);

        deserialized.Should().NotBeNull();
        deserialized!.ReviewIsolation.Should().Be(ReviewIsolation.Isolated);
    }

    [Fact]
    public void MessagePack_OldPayloadWithStaleKey3_AndNewKey4Shared_UsesKey4()
    {
        // When Key(4) is explicitly Shared (value 1), it should deserialize as Shared.
        var options = MessagePackSerializerOptions.Standard;

        // Construct 5-element array: [nil, nil, 2, 0, 1]
        // Key(3)=0 (stale, ignored), Key(4)=1 (Shared in new enum)
        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);

        writer.WriteArrayHeader(5);
        writer.WriteNil();  // Key(0): FixPrompt = null
        writer.WriteNil();  // Key(1): InlineComments = nil
        writer.Write(2);    // Key(2): MaxIterations = 2
        writer.Write(0);    // Key(3): stale (ignored)
        writer.Write(1);    // Key(4): ReviewIsolation = Shared (enum value 1)

        writer.Flush();
        var payload = bufferWriter.WrittenMemory.ToArray();

        var deserialized = MessagePackSerializer.Deserialize<CodeReviewConfiguration>(payload, options);

        deserialized.Should().NotBeNull();
        deserialized!.ReviewIsolation.Should().Be(ReviewIsolation.Shared);
    }
}
