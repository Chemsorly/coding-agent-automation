using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

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
}
