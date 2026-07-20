using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class IssueIdentifierTests
{
    [Fact]
    public void ImplicitConversion_FromString_ProducesCorrectValue()
    {
        IssueIdentifier id = "owner/repo#42";

        id.Value.Should().Be("owner/repo#42");
    }

    [Fact]
    public void ImplicitConversion_ToString_ProducesCorrectValue()
    {
        var id = new IssueIdentifier("owner/repo#42");

        string result = id;

        result.Should().Be("owner/repo#42");
    }

    [Fact]
    public void ToString_ReturnsInnerValue()
    {
        var id = new IssueIdentifier("org/repo#123");

        id.ToString().Should().Be("org/repo#123");
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        var id1 = new IssueIdentifier("same-issue");
        var id2 = new IssueIdentifier("same-issue");

        id1.Should().Be(id2);
        (id1 == id2).Should().BeTrue();
        id1.GetHashCode().Should().Be(id2.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentValues_AreNotEqual()
    {
        var id1 = new IssueIdentifier("issue-a");
        var id2 = new IssueIdentifier("issue-b");

        id1.Should().NotBe(id2);
        (id1 != id2).Should().BeTrue();
    }

    [Fact]
    public void Equality_ImplicitConversion_MatchesExplicitConstruction()
    {
        IssueIdentifier implicit1 = "owner/repo#1";
        var explicit1 = new IssueIdentifier("owner/repo#1");

        implicit1.Should().Be(explicit1);
    }

    [Fact]
    public void Default_HasNullValue()
    {
        // TODO: Add tests covering how a null-valued default(IssueIdentifier) flows through production code
        // (implicit string conversion returns null, ToString() returns null, JSON serialization with null Value).
        // Verify PipelineRunLifecycleService.IsIssueBeingProcessed guard behavior with default instance.
        var id = default(IssueIdentifier);

        id.Value.Should().BeNull();
    }

    [Fact]
    public void HashSet_WorksCorrectly_WithImplicitConversion()
    {
        var set = new HashSet<(IssueIdentifier IssueId, ProviderConfigId ProviderId)>
        {
            ("issue-1", "provider-a"),
            ("issue-2", "provider-b")
        };

        set.Should().HaveCount(2);
        set.Contains(((IssueIdentifier)"issue-1", (ProviderConfigId)"provider-a")).Should().BeTrue();
        set.Contains(((IssueIdentifier)"issue-3", (ProviderConfigId)"provider-c")).Should().BeFalse();
    }

    [Fact]
    public void HashSet_Deduplicates_SameTupleValues()
    {
        var set = new HashSet<(IssueIdentifier IssueId, ProviderConfigId ProviderId)>
        {
            ("issue-1", "provider-a"),
            ("issue-1", "provider-a") // duplicate
        };

        set.Should().HaveCount(1);
    }

    [Fact]
    public void JsonConverter_RoundTrip_SerializesAsBareString()
    {
        // TODO: Add test for null JSON token deserialization — reader.GetString()! in the converter
        // silently produces IssueIdentifier(null) instead of throwing JsonException. This is an untested
        // edge case relevant for DB-stored JSONB payloads.
        var original = new IssueIdentifier("owner/repo#42");
        var options = PipelineJsonOptions.Default;

        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<IssueIdentifier>(json, options);

        json.Should().Be("\"owner/repo#42\"");
        deserialized.Should().Be(original);
    }

    [Fact]
    public void JsonConverter_DeserializesFromBareString()
    {
        var json = "\"org/repo#99\"";
        var options = PipelineJsonOptions.Default;

        var result = JsonSerializer.Deserialize<IssueIdentifier>(json, options);

        result.Value.Should().Be("org/repo#99");
    }

    [Fact]
    public void JsonConverter_InRecord_RoundTrip()
    {
        var request = new TestRecord { IssueIdentifier = "owner/repo#7" };
        var options = PipelineJsonOptions.Default;

        var json = JsonSerializer.Serialize(request, options);
        var deserialized = JsonSerializer.Deserialize<TestRecord>(json, options);

        // Verify it serializes as a bare string, not {"value":"..."}
        json.Should().Contain("\"issueIdentifier\": \"owner/repo#7\"");
        deserialized!.IssueIdentifier.Value.Should().Be("owner/repo#7");
    }

    [Fact]
    public void BidirectionalImplicit_AllowsPassingToStringParam()
    {
        var id = new IssueIdentifier("test-issue");

        // Simulates passing to a method with string parameter
        string result = AcceptString(id);

        result.Should().Be("test-issue");
    }

    [Fact]
    public void BidirectionalImplicit_AllowsStringInterpolation()
    {
        var id = new IssueIdentifier("42");

        var result = $"Issue #{id}";

        result.Should().Be("Issue #42");
    }

    private static string AcceptString(string value) => value;

    private sealed record TestRecord
    {
        public required IssueIdentifier IssueIdentifier { get; init; }
    }
}
