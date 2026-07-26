using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class TemplateIdTests
{
    [Fact]
    public void ImplicitConversion_FromString_ProducesCorrectValue()
    {
        TemplateId id = "tmpl-123";

        id.Value.Should().Be("tmpl-123");
    }

    [Fact]
    public void ImplicitConversion_FromNull_ThrowsArgumentException()
    {
        var act = () => { TemplateId id = (string)null!; };

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ImplicitConversion_FromEmpty_ThrowsArgumentException()
    {
        var act = () => { TemplateId id = ""; };

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToString_ReturnsInnerValue()
    {
        var id = new TemplateId("tmpl-456");

        id.ToString().Should().Be("tmpl-456");
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        var id1 = new TemplateId("same-tmpl");
        var id2 = new TemplateId("same-tmpl");

        id1.Should().Be(id2);
        (id1 == id2).Should().BeTrue();
        id1.GetHashCode().Should().Be(id2.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentValues_AreNotEqual()
    {
        var id1 = new TemplateId("tmpl-a");
        var id2 = new TemplateId("tmpl-b");

        id1.Should().NotBe(id2);
        (id1 != id2).Should().BeTrue();
    }

    [Fact]
    public void Equality_ImplicitConversion_MatchesExplicitConstruction()
    {
        TemplateId implicit1 = "tmpl-1";
        var explicit1 = new TemplateId("tmpl-1");

        implicit1.Should().Be(explicit1);
    }

    [Fact]
    public void Default_HasNullValue()
    {
        var id = default(TemplateId);

        id.Value.Should().BeNull();
    }

    [Fact]
    public void HashSet_WorksCorrectly()
    {
        var set = new HashSet<TemplateId>
        {
            new TemplateId("tmpl-1"),
            new TemplateId("tmpl-2")
        };

        set.Should().HaveCount(2);
        set.Contains(new TemplateId("tmpl-1")).Should().BeTrue();
        set.Contains(new TemplateId("tmpl-3")).Should().BeFalse();
    }

    [Fact]
    public void HashSet_Deduplicates_SameValues()
    {
        var set = new HashSet<TemplateId>
        {
            "tmpl-1",
            "tmpl-1" // duplicate via implicit conversion
        };

        set.Should().HaveCount(1);
    }
}
