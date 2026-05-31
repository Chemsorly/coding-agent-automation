using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class ReviewCommentValidationTests
{
    [Fact]
    public void Path_Null_ThrowsArgumentNullException()
    {
        var act = () => new ReviewComment { Path = null!, Line = 1, Body = "text" };
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Path_EmptyOrWhitespace_ThrowsArgumentException()
    {
        var act = () => new ReviewComment { Path = "  ", Line = 1, Body = "text" };
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Path_WithBackslash_ThrowsArgumentException()
    {
        var act = () => new ReviewComment { Path = @"src\file.cs", Line = 1, Body = "text" };
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Line_LessThanOne_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new ReviewComment { Path = "src/file.cs", Line = 0, Body = "text" };
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Body_Null_ThrowsArgumentNullException()
    {
        var act = () => new ReviewComment { Path = "src/file.cs", Line = 1, Body = null! };
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Body_ExceedsMaxLength_ThrowsArgumentException()
    {
        var act = () => new ReviewComment { Path = "src/file.cs", Line = 1, Body = new string('a', 65537) };
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ValidConstruction_SetsAllProperties()
    {
        var comment = new ReviewComment { Path = "src/file.cs", Line = 42, Side = DiffSide.Left, Body = "review" };

        comment.Path.Should().Be("src/file.cs");
        comment.Line.Should().Be(42);
        comment.Side.Should().Be(DiffSide.Left);
        comment.Body.Should().Be("review");
    }
}
