using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.CodeReview.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class InlineCommentFormatterTests
{
    private static StructuredFinding MakeFinding(
        FindingSeverity severity = FindingSeverity.Warning,
        string message = "issue found",
        string agentName = "TestAgent") => new()
    {
        Severity = severity,
        FilePath = "src/file.cs",
        LineNumber = 1,
        Message = message,
        AgentName = agentName
    };

    [Fact]
    public void FormatSingle_NullInput_ThrowsArgumentNullException()
    {
        var act = () => InlineCommentFormatter.FormatSingle(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FormatSingle_Critical_ProducesCorrectFormat()
    {
        var finding = MakeFinding(FindingSeverity.Critical, "memory leak", "SecurityAgent");

        var result = InlineCommentFormatter.FormatSingle(finding);

        result.Should().Be("🔴 **CRITICAL**: memory leak\n— *SecurityAgent*");
    }

    [Fact]
    public void FormatSingle_Warning_ProducesCorrectEmoji()
    {
        var result = InlineCommentFormatter.FormatSingle(MakeFinding(FindingSeverity.Warning));
        result.Should().StartWith("🟡 **WARNING**");
    }

    [Fact]
    public void FormatSingle_Suggestion_ProducesCorrectEmoji()
    {
        var result = InlineCommentFormatter.FormatSingle(MakeFinding(FindingSeverity.Suggestion));
        result.Should().StartWith("💡 **SUGGESTION**");
    }

    [Fact]
    public void FormatConsolidated_NullInput_ThrowsArgumentNullException()
    {
        var act = () => InlineCommentFormatter.FormatConsolidated(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FormatConsolidated_EmptyList_ReturnsEmptyString()
    {
        var result = InlineCommentFormatter.FormatConsolidated([]);
        result.Should().BeEmpty();
    }

    [Fact]
    public void FormatConsolidated_SingleFinding_DelegatesToFormatSingle()
    {
        var finding = MakeFinding(FindingSeverity.Critical, "bug", "Agent1");

        var consolidated = InlineCommentFormatter.FormatConsolidated([finding]);
        var single = InlineCommentFormatter.FormatSingle(finding);

        consolidated.Should().Be(single);
    }

    [Fact]
    public void FormatConsolidated_MultipleFindings_OrdersBySeverityAndSeparates()
    {
        var findings = new[]
        {
            MakeFinding(FindingSeverity.Suggestion, "style", "A"),
            MakeFinding(FindingSeverity.Critical, "bug", "B")
        };

        var result = InlineCommentFormatter.FormatConsolidated(findings);

        result.Should().StartWith("🔴 **CRITICAL**");
        result.Should().Contain("\n\n---\n\n");
        result.Should().Contain("💡 **SUGGESTION**");
    }

    [Fact]
    public void FormatConsolidated_TruncatesAt65536Characters()
    {
        // Create findings with long messages that will exceed the limit
        var longMessage = new string('x', 60000);
        var findings = new[]
        {
            MakeFinding(FindingSeverity.Critical, longMessage, "A"),
            MakeFinding(FindingSeverity.Warning, longMessage, "B")
        };

        var result = InlineCommentFormatter.FormatConsolidated(findings);

        result.Length.Should().BeLessThanOrEqualTo(65536);
        // Only the first finding should fit
        result.Should().NotContain("🟡 **WARNING**");
    }
}
