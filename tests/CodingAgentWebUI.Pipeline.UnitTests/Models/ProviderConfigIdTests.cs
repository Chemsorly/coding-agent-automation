using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class ProviderConfigIdTests
{
    [Fact]
    public void ImplicitConversion_FromString_ProducesCorrectValue()
    {
        // TODO: Add a test for implicit conversion from null string. The implicit operator accepts
        // null without validation, producing ProviderConfigId with Value = null. This is a meaningful
        // boundary that the old code explicitly guarded against with ArgumentNullException.ThrowIfNull.
        ProviderConfigId id = "my-provider-config";

        id.Value.Should().Be("my-provider-config");
    }

    [Fact]
    public void ToString_ReturnsInnerValue()
    {
        var id = new ProviderConfigId("config-123");

        id.ToString().Should().Be("config-123");
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        var id1 = new ProviderConfigId("same-id");
        var id2 = new ProviderConfigId("same-id");

        id1.Should().Be(id2);
        (id1 == id2).Should().BeTrue();
        id1.GetHashCode().Should().Be(id2.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentValues_AreNotEqual()
    {
        var id1 = new ProviderConfigId("id-a");
        var id2 = new ProviderConfigId("id-b");

        id1.Should().NotBe(id2);
        (id1 != id2).Should().BeTrue();
    }

    [Fact]
    public void Equality_ImplicitConversion_MatchesExplicitConstruction()
    {
        ProviderConfigId implicit1 = "provider-1";
        var explicit1 = new ProviderConfigId("provider-1");

        implicit1.Should().Be(explicit1);
    }

    [Fact]
    public void Default_HasNullValue()
    {
        // TODO: Add a test that validates how production code (LabelService, PipelineOrchestrationService)
        // handles a default/null-valued ProviderConfigId. Currently default(ProviderConfigId) passes
        // silently through method signatures and would cause NullReferenceException deeper in the call chain.
        var id = default(ProviderConfigId);

        id.Value.Should().BeNull();
    }

    [Fact]
    public void HashSet_WorksCorrectly_WithImplicitConversion()
    {
        var set = new HashSet<(string IssueId, ProviderConfigId ProviderId)>
        {
            ("issue-1", "provider-a"),
            ("issue-2", "provider-b")
        };

        set.Should().HaveCount(2);
        set.Contains(("issue-1", (ProviderConfigId)"provider-a")).Should().BeTrue();
        set.Contains(("issue-3", (ProviderConfigId)"provider-c")).Should().BeFalse();
    }

    [Fact]
    public void HashSet_Deduplicates_SameTupleValues()
    {
        var set = new HashSet<(string IssueId, ProviderConfigId ProviderId)>
        {
            ("issue-1", "provider-a"),
            ("issue-1", "provider-a") // duplicate
        };

        set.Should().HaveCount(1);
    }
}
