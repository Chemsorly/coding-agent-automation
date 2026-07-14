using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="AnalysisBodyHash"/> utility.
/// </summary>
public class AnalysisBodyHashTests
{
    [Fact]
    public void Compute_IsDeterministic_SameInputProducesSameOutput()
    {
        var hash1 = AnalysisBodyHash.Compute("test body content");
        var hash2 = AnalysisBodyHash.Compute("test body content");
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Compute_TrimsWhitespace_ProducesSameHash()
    {
        var hash1 = AnalysisBodyHash.Compute("body");
        var hash2 = AnalysisBodyHash.Compute("  body  ");
        var hash3 = AnalysisBodyHash.Compute("\n\tbody\n\t");
        hash1.Should().Be(hash2);
        hash1.Should().Be(hash3);
    }

    [Fact]
    public void Compute_NullBody_ProducesConsistentHash()
    {
        var hashNull = AnalysisBodyHash.Compute(null);
        var hashEmpty = AnalysisBodyHash.Compute("");
        var hashWhitespace = AnalysisBodyHash.Compute("   ");
        hashNull.Should().Be(hashEmpty);
        hashNull.Should().Be(hashWhitespace);
    }

    [Fact]
    public void Compute_ProducesLowercaseHex_12Characters()
    {
        var hash = AnalysisBodyHash.Compute("any text");
        hash.Should().HaveLength(12);
        hash.Should().MatchRegex("^[a-f0-9]{12}$");
    }

    [Fact]
    public void Compute_DifferentInputs_ProduceDifferentHashes()
    {
        var hash1 = AnalysisBodyHash.Compute("body A");
        var hash2 = AnalysisBodyHash.Compute("body B");
        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void Extract_ValidMarker_ReturnsHash()
    {
        var comment = "## 🤖 Agent Analysis\n\nSome content\n\n<!-- agent:analysis-body-hash:abc123def456 -->";
        var result = AnalysisBodyHash.Extract(comment);
        result.Should().Be("abc123def456");
    }

    [Fact]
    public void Extract_NoMarker_ReturnsNull()
    {
        var comment = "## 🤖 Agent Analysis\n\nSome content without hash marker";
        var result = AnalysisBodyHash.Extract(comment);
        result.Should().BeNull();
    }

    [Fact]
    public void Extract_MultipleLinesNoMarker_ReturnsNull()
    {
        var comment = "Line 1\nLine 2\n<!-- some other marker -->\nLine 4";
        var result = AnalysisBodyHash.Extract(comment);
        result.Should().BeNull();
    }

    [Fact]
    public void Extract_InvalidHashLength_ReturnsNull()
    {
        var comment = "<!-- agent:analysis-body-hash:abc -->";
        var result = AnalysisBodyHash.Extract(comment);
        result.Should().BeNull();
    }

    [Fact]
    public void Compute_WhitespaceOnlyBody_ProducesConsistentHash()
    {
        var hash1 = AnalysisBodyHash.Compute("   \t\n  ");
        var hash2 = AnalysisBodyHash.Compute("");
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void RoundTrip_ComputeAndExtract_Match()
    {
        var body = "Some issue description";
        var hash = AnalysisBodyHash.Compute(body);
        var comment = $"## Analysis\n<!-- agent:analysis-body-hash:{hash} -->";
        var extracted = AnalysisBodyHash.Extract(comment);
        extracted.Should().Be(hash);
    }
}
