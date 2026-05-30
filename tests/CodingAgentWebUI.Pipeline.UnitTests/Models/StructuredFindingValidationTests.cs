using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class StructuredFindingValidationTests
{
    [Fact]
    public void FilePath_WithBackslash_ThrowsArgumentException()
    {
        var act = () => new StructuredFinding
        {
            Severity = FindingSeverity.Warning, FilePath = @"src\file.cs",
            LineNumber = 1, Message = "msg", AgentName = "A"
        };
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void LineNumber_Negative_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new StructuredFinding
        {
            Severity = FindingSeverity.Warning, FilePath = "src/file.cs",
            LineNumber = -1, Message = "msg", AgentName = "A"
        };
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Message_Null_ThrowsArgumentNullException()
    {
        var act = () => new StructuredFinding
        {
            Severity = FindingSeverity.Warning, FilePath = "src/file.cs",
            LineNumber = 1, Message = null!, AgentName = "A"
        };
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Message_ExceedsMaxLength_ThrowsArgumentException()
    {
        var act = () => new StructuredFinding
        {
            Severity = FindingSeverity.Warning, FilePath = "src/file.cs",
            LineNumber = 1, Message = new string('x', 65537), AgentName = "A"
        };
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AgentName_Null_ThrowsArgumentNullException()
    {
        var act = () => new StructuredFinding
        {
            Severity = FindingSeverity.Warning, FilePath = "src/file.cs",
            LineNumber = 1, Message = "msg", AgentName = null!
        };
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ValidConstruction_WithNullFilePath_Succeeds()
    {
        var finding = new StructuredFinding
        {
            Severity = FindingSeverity.Critical, FilePath = null,
            LineNumber = 0, Message = "general issue", AgentName = "ReviewBot"
        };

        finding.Severity.Should().Be(FindingSeverity.Critical);
        finding.FilePath.Should().BeNull();
        finding.LineNumber.Should().Be(0);
        finding.Message.Should().Be("general issue");
        finding.AgentName.Should().Be("ReviewBot");
    }
}
