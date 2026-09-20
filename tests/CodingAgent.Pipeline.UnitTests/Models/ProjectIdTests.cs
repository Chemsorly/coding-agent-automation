using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

public class ProjectIdTests
{
    [Fact]
    public void ImplicitConversion_FromString_ProducesCorrectValue()
    {
        ProjectId id = "my-project";

        id.Value.Should().Be("my-project");
    }

    [Fact]
    public void ImplicitConversion_FromNull_ThrowsArgumentException()
    {
        var act = () => { ProjectId id = (string)null!; };

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ImplicitConversion_FromEmpty_ThrowsArgumentException()
    {
        var act = () => { ProjectId id = ""; };

        act.Should().Throw<ArgumentException>();
    }

    // TODO: [WARNING] ArgumentException.ThrowIfNullOrEmpty does NOT reject whitespace-only strings.
    // A value like "   " produces a ProjectId with Value == "   ", which silently fails Guid.TryParse
    // in PostgresConfigurationStore (silent no-op) or produces unexpected HTTP payloads via .Value.
    // It is undocumented whether whitespace is intentionally allowed. Add a test covering whitespace-only
    // input and, if rejection is desired, switch to ArgumentException.ThrowIfNullOrWhiteSpace.

    [Fact]
    public void ToString_ReturnsInnerValue()
    {
        var id = new ProjectId("project-123");

        id.ToString().Should().Be("project-123");
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        var id1 = new ProjectId("same-id");
        var id2 = new ProjectId("same-id");

        id1.Should().Be(id2);
        (id1 == id2).Should().BeTrue();
        id1.GetHashCode().Should().Be(id2.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentValues_AreNotEqual()
    {
        var id1 = new ProjectId("id-a");
        var id2 = new ProjectId("id-b");

        id1.Should().NotBe(id2);
        (id1 != id2).Should().BeTrue();
    }

    [Fact]
    public void Equality_ImplicitConversion_MatchesExplicitConstruction()
    {
        ProjectId implicit1 = "project-1";
        var explicit1 = new ProjectId("project-1");

        implicit1.Should().Be(explicit1);
    }

    // TODO: [WARNING] This test documents an invalid object state (null Value) as expected/acceptable behavior.
    // Treating default(ProjectId) as a valid state makes it harder to add constructor validation in a
    // follow-up phase without breaking this test. Consider renaming to Default_ValueIsNull_KnownLimitation
    // and adding a comment clarifying this is a known design gap, not intended behavior. When constructor
    // validation is added, this test should be updated to expect a throw or be replaced with a test that
    // asserts default(ProjectId) is unusable.
    [Fact]
    public void Default_HasNullValue()
    {
        var id = default(ProjectId);

        id.Value.Should().BeNull();
    }
}
