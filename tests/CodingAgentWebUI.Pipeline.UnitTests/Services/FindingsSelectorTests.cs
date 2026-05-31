using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.CodeReview.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class FindingsSelectorTests
{
    private static StructuredFinding MakeFinding(
        FindingSeverity severity = FindingSeverity.Warning,
        string? filePath = "src/file.cs",
        int lineNumber = 10,
        string message = "issue",
        string agentName = "Agent") => new()
    {
        Severity = severity,
        FilePath = filePath,
        LineNumber = lineNumber,
        Message = message,
        AgentName = agentName
    };

    private static InlineCommentSettings DefaultSettings(
        FindingSeverity threshold = FindingSeverity.Warning,
        int max = 15,
        bool orderBySeverity = true) => new()
    {
        SeverityThreshold = threshold,
        MaxInlineComments = max,
        OrderBySeverity = orderBySeverity
    };

    [Fact]
    public void Select_NullFindings_ThrowsArgumentNullException()
    {
        var act = () => FindingsSelector.Select(null!, DefaultSettings());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Select_NullSettings_ThrowsArgumentNullException()
    {
        var act = () => FindingsSelector.Select([], null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Select_EmptyInput_ReturnsEmptyWithZeroExcluded()
    {
        var (comments, excluded) = FindingsSelector.Select([], DefaultSettings());

        comments.Should().BeEmpty();
        excluded.Should().Be(0);
    }

    [Fact]
    public void Select_FindingsWithNullFilePath_AreExcluded()
    {
        var findings = new[]
        {
            MakeFinding(filePath: null, lineNumber: 0),
            MakeFinding(filePath: "src/a.cs", lineNumber: 5)
        };

        var (comments, _) = FindingsSelector.Select(findings, DefaultSettings(threshold: FindingSeverity.Suggestion));

        comments.Should().HaveCount(1);
        comments[0].Path.Should().Be("src/a.cs");
    }

    [Fact]
    public void Select_ThresholdFiltering_ExcludesBelowThreshold()
    {
        var findings = new[]
        {
            MakeFinding(severity: FindingSeverity.Suggestion, filePath: "a.cs", lineNumber: 1),
            MakeFinding(severity: FindingSeverity.Warning, filePath: "b.cs", lineNumber: 2),
            MakeFinding(severity: FindingSeverity.Critical, filePath: "c.cs", lineNumber: 3)
        };

        var (comments, _) = FindingsSelector.Select(findings, DefaultSettings(threshold: FindingSeverity.Warning));

        comments.Should().HaveCount(2);
        comments.Select(c => c.Path).Should().BeEquivalentTo(["b.cs", "c.cs"]);
    }

    [Fact]
    public void Select_OrderBySeverityTrue_CriticalFirst()
    {
        var findings = new[]
        {
            MakeFinding(severity: FindingSeverity.Warning, filePath: "a.cs", lineNumber: 1),
            MakeFinding(severity: FindingSeverity.Critical, filePath: "b.cs", lineNumber: 2),
            MakeFinding(severity: FindingSeverity.Warning, filePath: "c.cs", lineNumber: 3)
        };

        var (comments, _) = FindingsSelector.Select(findings, DefaultSettings(orderBySeverity: true));

        comments[0].Path.Should().Be("b.cs");
    }

    [Fact]
    public void Select_OrderBySeverityFalse_PreservesOriginalOrder()
    {
        var findings = new[]
        {
            MakeFinding(severity: FindingSeverity.Warning, filePath: "a.cs", lineNumber: 1),
            MakeFinding(severity: FindingSeverity.Critical, filePath: "b.cs", lineNumber: 2)
        };

        var (comments, _) = FindingsSelector.Select(findings, DefaultSettings(orderBySeverity: false));

        comments[0].Path.Should().Be("a.cs");
        comments[1].Path.Should().Be("b.cs");
    }

    [Fact]
    public void Select_CapEnforcement_ReturnsCorrectExcludedCount()
    {
        var findings = Enumerable.Range(1, 5)
            .Select(i => MakeFinding(filePath: $"f{i}.cs", lineNumber: i))
            .ToList();

        var (comments, excluded) = FindingsSelector.Select(findings, DefaultSettings(max: 3));

        comments.Should().HaveCount(3);
        excluded.Should().Be(2);
    }

    [Fact]
    public void Select_MaxClampedToOne_WhenBelowRange()
    {
        var findings = new[] { MakeFinding(), MakeFinding(filePath: "b.cs", lineNumber: 2) };

        var (comments, excluded) = FindingsSelector.Select(findings, DefaultSettings(max: 0));

        comments.Should().HaveCount(1);
        excluded.Should().Be(1);
    }

    [Fact]
    public void Select_MaxClampedToFifty_WhenAboveRange()
    {
        var findings = Enumerable.Range(1, 55)
            .Select(i => MakeFinding(filePath: $"f{i}.cs", lineNumber: i))
            .ToList();

        var (comments, excluded) = FindingsSelector.Select(findings, DefaultSettings(max: 100));

        excluded.Should().Be(5);
    }

    [Fact]
    public void Select_Consolidation_GroupsSameFileAndLine()
    {
        var findings = new[]
        {
            MakeFinding(filePath: "src/a.cs", lineNumber: 10, message: "first"),
            MakeFinding(filePath: "src/a.cs", lineNumber: 10, message: "second"),
            MakeFinding(filePath: "src/b.cs", lineNumber: 5, message: "third")
        };

        var (comments, _) = FindingsSelector.Select(findings, DefaultSettings(threshold: FindingSeverity.Suggestion));

        comments.Should().HaveCount(2);
    }

    [Fact]
    public void Select_AllComments_HaveSideRight()
    {
        var findings = new[]
        {
            MakeFinding(filePath: "a.cs", lineNumber: 1),
            MakeFinding(filePath: "b.cs", lineNumber: 2)
        };

        var (comments, _) = FindingsSelector.Select(findings, DefaultSettings());

        comments.Should().AllSatisfy(c => c.Side.Should().Be(DiffSide.Right));
    }
}
