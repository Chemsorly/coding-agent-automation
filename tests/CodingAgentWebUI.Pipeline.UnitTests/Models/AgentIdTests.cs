using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

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
    public void ImplicitConversion_FromNull_ThrowsArgumentNullException()
    {
        var act = () => { AgentId id = (string)null!; };

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ImplicitConversion_FromEmpty_ThrowsArgumentException()
    {
        var act = () => { AgentId id = ""; };

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToString_ReturnsInnerValue()
    {
        var id = new AgentId("agent-456");

        id.ToString().Should().Be("agent-456");
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
    public void Equality_SameValue_AreEqual()
    {
        var id1 = new AgentId("same-agent");
        var id2 = new AgentId("same-agent");

        id1.Should().Be(id2);
        (id1 == id2).Should().BeTrue();
        id1.GetHashCode().Should().Be(id2.GetHashCode());
    }

    [Fact]
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
}

/// <summary>
/// Tests for the MessagePack formatter that serializes <see cref="AgentId"/> as a bare string.
/// </summary>
public class AgentIdFormatterTests
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                new IMessagePackFormatter[] { new AgentIdFormatter() },
                new IFormatterResolver[] { ContractlessStandardResolverAllowPrivate.Instance }));

    [Fact]
    public void RoundTrip_SerializesAsString_DeserializesBackToAgentId()
    {
        var original = new AgentId("abc-123");

        var bytes = MessagePackSerializer.Serialize(original, Options);
        var deserialized = MessagePackSerializer.Deserialize<AgentId>(bytes, Options);

        deserialized.Should().Be(original);
        deserialized.Value.Should().Be("abc-123");
    }

    [Fact]
    public void Serialize_ProducesPlainString_NotMapFormat()
    {
        var id = new AgentId("test-agent");

        var bytes = MessagePackSerializer.Serialize(id, Options);
        // Deserialize as a raw string to prove it's stored as a bare string
        var asString = MessagePackSerializer.Deserialize<string>(bytes, Options);

        asString.Should().Be("test-agent");
    }

    [Fact]
    public void Serialize_DefaultAgentId_ThrowsMessagePackSerializationException()
    {
        var defaultId = default(AgentId);

        var act = () => MessagePackSerializer.Serialize(defaultId, Options);

        act.Should().Throw<MessagePackSerializationException>();
    }

    [Fact]
    public void Deserialize_FromPlainString_ProducesAgentId()
    {
        // Serialize a raw string, then deserialize as AgentId
        var bytes = MessagePackSerializer.Serialize("raw-string-agent", Options);
        var deserialized = MessagePackSerializer.Deserialize<AgentId>(bytes, Options);

        deserialized.Value.Should().Be("raw-string-agent");
    }

    [Fact]
    public void Deserialize_FromNilToken_ThrowsMessagePackSerializationException()
    {
        // Serialize a null string to produce a nil MessagePack token
        var bytes = MessagePackSerializer.Serialize((string?)null, Options);

        var act = () => MessagePackSerializer.Deserialize<AgentId>(bytes, Options);

        act.Should().Throw<MessagePackSerializationException>();
    }

    [Fact]
    public void RoundTrip_GuidFormatAgentId()
    {
        var guid = Guid.NewGuid().ToString();
        var original = new AgentId(guid);

        var bytes = MessagePackSerializer.Serialize(original, Options);
        var deserialized = MessagePackSerializer.Deserialize<AgentId>(bytes, Options);

        deserialized.Value.Should().Be(guid);
    }

    [Fact]
    public void Deserialize_FromEmptyString_ProducesAgentIdWithEmptyValue()
    {
        // NOTE: The Deserialize path does NOT call the implicit operator, so empty strings pass through.
        // This is a known gap (documented in AgentId.cs TODO): the type's invariant is not enforced
        // at the deserialization boundary. This test documents the current behavior.
        var bytes = MessagePackSerializer.Serialize(string.Empty, Options);
        var deserialized = MessagePackSerializer.Deserialize<AgentId>(bytes, Options);

        deserialized.Value.Should().BeEmpty();
    }
}
