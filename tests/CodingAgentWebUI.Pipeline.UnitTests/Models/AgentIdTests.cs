using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using MessagePack;
using System.Buffers;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class AgentIdTests
{
    [Fact]
    public void ImplicitConversion_FromString_ProducesCorrectValue()
    {
        AgentId id = "agent-123";

        id.Value.Should().Be("agent-123");
    }

    [Fact]
    public void ToString_ReturnsInnerValue()
    {
        var id = new AgentId("agent-456");

        id.ToString().Should().Be("agent-456");
    }

    [Fact]
    // TODO: This test exercises compiler-generated record struct equality rather than custom AgentId logic.
    // Consider replacing with tests that verify custom behavior (implicit conversion + equality interaction).
    public void Equality_SameValue_AreEqual()
    {
        var id1 = new AgentId("same-agent");
        var id2 = new AgentId("same-agent");

        id1.Should().Be(id2);
        (id1 == id2).Should().BeTrue();
        id1.GetHashCode().Should().Be(id2.GetHashCode());
    }

    [Fact]
    // TODO: This test exercises compiler-generated record struct inequality — same concern as Equality_SameValue_AreEqual.
    public void Equality_DifferentValues_AreNotEqual()
    {
        var id1 = new AgentId("agent-a");
        var id2 = new AgentId("agent-b");

        id1.Should().NotBe(id2);
        (id1 != id2).Should().BeTrue();
    }

    [Fact]
    public void Equality_ImplicitConversion_MatchesExplicitConstruction()
    {
        AgentId implicit1 = "agent-1";
        var explicit1 = new AgentId("agent-1");

        implicit1.Should().Be(explicit1);
    }

    [Fact]
    public void DefaultToString_ReturnsEmptyString()
    {
        var id = default(AgentId);

        id.ToString().Should().Be(string.Empty);
    }

    [Fact]
    public void Default_HasNullValue()
    {
        var id = default(AgentId);

        id.Value.Should().BeNull();
    }

    [Fact]
    // TODO: HashSet tests below exercise compiler-generated GetHashCode/Equals from record struct,
    // not custom AgentId logic. They document expected collection behavior but wouldn't detect regressions
    // in custom code.
    public void HashSet_WorksCorrectly()
    {
        var set = new HashSet<AgentId>
        {
            new AgentId("agent-1"),
            new AgentId("agent-2")
        };

        set.Should().HaveCount(2);
        set.Contains(new AgentId("agent-1")).Should().BeTrue();
        set.Contains(new AgentId("agent-3")).Should().BeFalse();
    }

    [Fact]
    public void HashSet_Deduplicates_SameValues()
    {
        var set = new HashSet<AgentId>
        {
            "agent-1",
            "agent-1" // duplicate via implicit conversion
        };

        set.Should().HaveCount(1);
    }

    [Fact]
    public void ImplicitConversion_NullString_Throws()
    {
        var act = () => { AgentId id = null!; };

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ImplicitConversion_EmptyString_Throws()
    {
        var act = () => { AgentId id = string.Empty; };

        act.Should().Throw<ArgumentException>();
    }
}

public class AgentIdFormatterTests
{
    private readonly AgentIdFormatter _formatter = new();

    [Fact]
    public void Serialize_WritesValueAsBareString()
    {
        var id = new AgentId("agent-42");
        var writer = new ArrayBufferWriter<byte>();
        var msgpackWriter = new MessagePackWriter(writer);

        _formatter.Serialize(ref msgpackWriter, id, MessagePackSerializerOptions.Standard);
        msgpackWriter.Flush();

        // Deserialize the bytes back as a plain string to verify it's a bare string on the wire
        var reader = new MessagePackReader(writer.WrittenMemory);
        var decoded = reader.ReadString();
        decoded.Should().Be("agent-42");
    }

    [Fact]
    public void Deserialize_FromBareStringBytes_ProducesCorrectAgentId()
    {
        // Encode "agent-99" as a bare MessagePack string
        var writer = new ArrayBufferWriter<byte>();
        var msgpackWriter = new MessagePackWriter(writer);
        msgpackWriter.Write("agent-99");
        msgpackWriter.Flush();

        var reader = new MessagePackReader(writer.WrittenMemory);
        var id = _formatter.Deserialize(ref reader, MessagePackSerializerOptions.Standard);

        id.Value.Should().Be("agent-99");
    }

    [Fact]
    public void RoundTrip_SerializeThenDeserialize_PreservesValue()
    {
        var original = new AgentId("agent-roundtrip");
        var writer = new ArrayBufferWriter<byte>();
        var msgpackWriter = new MessagePackWriter(writer);

        _formatter.Serialize(ref msgpackWriter, original, MessagePackSerializerOptions.Standard);
        msgpackWriter.Flush();

        var reader = new MessagePackReader(writer.WrittenMemory);
        var restored = _formatter.Deserialize(ref reader, MessagePackSerializerOptions.Standard);

        restored.Should().Be(original);
        restored.Value.Should().Be("agent-roundtrip");
    }

    [Fact]
    public void Serialize_NullValue_Throws()
    {
        var id = default(AgentId); // Value is null
        var bufferWriter = new ArrayBufferWriter<byte>();
        var msgpackWriter = new MessagePackWriter(bufferWriter);

        MessagePackSerializationException? caughtEx = null;
        try
        {
            _formatter.Serialize(ref msgpackWriter, id, MessagePackSerializerOptions.Standard);
        }
        catch (MessagePackSerializationException ex)
        {
            caughtEx = ex;
        }

        caughtEx.Should().NotBeNull();
        caughtEx!.Message.Should().Contain("AgentId cannot serialize a null Value");
    }
}

// TODO: Add Deserialize_NilToken_Throws test to AgentIdFormatterTests — the Deserialize path that
// throws MessagePackSerializationException on a nil wire token has no test coverage. A regression
// that swallowed nil and returned default(AgentId) would go undetected.

// TODO: Add an end-to-end CompositeResolver test that verifies MessagePackSerializer.Serialize/
// Deserialize<AgentId>(value, options) using the full registered resolver options (matching the
// configuration in SignalRRegistration.cs and HubConnectionManager.cs) produces a bare string
// on the wire rather than a map {"Value":"..."}. Without this, a regression where the formatter
// is de-registered or resolver order is wrong would not be caught by existing tests.
