using AwesomeAssertions;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class CommentMarkersTests
{
    [Fact]
    public void IsPipelineGeneratedComment_Null_ReturnsFalse()
    {
        CommentMarkers.IsPipelineGeneratedComment(null).Should().BeFalse();
    }

    [Fact]
    public void IsPipelineGeneratedComment_Empty_ReturnsFalse()
    {
        CommentMarkers.IsPipelineGeneratedComment(string.Empty).Should().BeFalse();
    }

    [Fact]
    public void IsPipelineGeneratedComment_PipelinePrefix_ReturnsTrue()
    {
        CommentMarkers.IsPipelineGeneratedComment("## 🤖 Agent Analysis\nSome content").Should().BeTrue();
    }

    [Fact]
    public void IsPipelineGeneratedComment_AgentCommentPrefix_ReturnsTrue()
    {
        CommentMarkers.IsPipelineGeneratedComment("Some text\n<!-- agent:gate-rejection -->").Should().BeTrue();
    }

    [Fact]
    public void IsPipelineGeneratedComment_RegularComment_ReturnsFalse()
    {
        CommentMarkers.IsPipelineGeneratedComment("This is a normal user comment").Should().BeFalse();
    }
}
