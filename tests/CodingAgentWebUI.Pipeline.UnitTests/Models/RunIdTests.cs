using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class RunIdTests
{
    [Fact]
    public void ImplicitConversion_FromString_ProducesCorrectValue()
    {
        RunId id = "run-123";

        id.Value.Should().Be("run-123");
    }

    [Fact]
    public void ToString_ReturnsInnerValue()
    {
        var id = new RunId("run-456");

        id.ToString().Should().Be("run-456");
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        var id1 = new RunId("same-run");
        var id2 = new RunId("same-run");

        id1.Should().Be(id2);
        (id1 == id2).Should().BeTrue();
        id1.GetHashCode().Should().Be(id2.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentValues_AreNotEqual()
    {
        var id1 = new RunId("run-a");
        var id2 = new RunId("run-b");

        id1.Should().NotBe(id2);
        (id1 != id2).Should().BeTrue();
    }

    [Fact]
    public void Equality_ImplicitConversion_MatchesExplicitConstruction()
    {
        RunId implicit1 = "run-1";
        var explicit1 = new RunId("run-1");

        implicit1.Should().Be(explicit1);
    }

    // TODO: Add boundary tests verifying that lifecycle/service methods (GetRun, RemoveRun, FailRunAsync, etc.)
    // reject empty-string RunId with ArgumentException, since production code changed from
    // ThrowIfNull(runId) to ThrowIfNullOrEmpty(runId.Value) — this is a behavioral change that is untested.
    // Also add integration-level test verifying that default(RunId) is rejected by consumer methods.

    [Fact]
    public void Default_HasNullValue()
    {
        var id = default(RunId);

        id.Value.Should().BeNull();
    }

    [Fact]
    public void HashSet_WorksCorrectly()
    {
        var set = new HashSet<RunId>
        {
            new RunId("run-1"),
            new RunId("run-2")
        };

        set.Should().HaveCount(2);
        set.Contains(new RunId("run-1")).Should().BeTrue();
        set.Contains(new RunId("run-3")).Should().BeFalse();
    }

    [Fact]
    public void HashSet_Deduplicates_SameValues()
    {
        var set = new HashSet<RunId>
        {
            "run-1",
            "run-1" // duplicate via implicit conversion
        };

        set.Should().HaveCount(1);
    }
}
