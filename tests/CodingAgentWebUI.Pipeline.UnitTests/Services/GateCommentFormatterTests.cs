using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for GateCommentFormatter.FormatGateComment.
/// </summary>
public sealed class GateCommentFormatterTests
{
    private readonly GateCommentFormatter _sut = new(new Mock<ILogger>().Object);

    // ── Null/empty JSON ───────────────────────────────────────────────────

    [Fact]
    public void FormatGateComment_NullJson_WontDo_ReturnsFallback()
    {
        var result = _sut.FormatGateComment(null, isWontDo: true);
        result.Should().Contain("Won't Do");
    }

    [Fact]
    public void FormatGateComment_NullJson_NotReady_ReturnsFallback()
    {
        var result = _sut.FormatGateComment(null, isWontDo: false);
        result.Should().Contain("Needs Refinement");
    }

    [Fact]
    public void FormatGateComment_EmptyJson_WontDo_ReturnsFallback()
    {
        var result = _sut.FormatGateComment("", isWontDo: true);
        result.Should().Contain("Won't Do");
    }

    [Fact]
    public void FormatGateComment_WhitespaceJson_ReturnsFallback()
    {
        var result = _sut.FormatGateComment("   ", isWontDo: false);
        result.Should().Contain("Needs Refinement");
    }

    // ── Invalid JSON ──────────────────────────────────────────────────────

    [Fact]
    public void FormatGateComment_InvalidJson_WontDo_FallsBackToCodeBlock()
    {
        var result = _sut.FormatGateComment("not-json", isWontDo: true);
        result.Should().Contain("Won't Do");
        result.Should().Contain("not-json");
    }

    [Fact]
    public void FormatGateComment_InvalidJson_NotReady_FallsBackToCodeBlock()
    {
        var result = _sut.FormatGateComment("{invalid", isWontDo: false);
        result.Should().Contain("Needs Refinement");
        result.Should().Contain("{invalid");
    }

    // ── Valid assessment JSON ─────────────────────────────────────────────

    [Fact]
    public void FormatGateComment_ValidAssessmentJson_WontDo_ReturnsFormattedComment()
    {
        var json = """{"Recommendation":"wont_do","Summary":"Out of scope","Concerns":[],"BlockingIssues":[],"ConfidenceScore":0.9}""";
        var result = _sut.FormatGateComment(json, isWontDo: true);
        result.Should().NotBeNullOrWhiteSpace();
        result.Should().NotContain("```json"); // should not fall back to code block
    }

    [Fact]
    public void FormatGateComment_ValidAssessmentJson_NotReady_ReturnsFormattedComment()
    {
        var json = """{"Recommendation":"not_ready","Summary":"Needs more detail","Concerns":["Unclear scope"],"BlockingIssues":[],"ConfidenceScore":0.6}""";
        var result = _sut.FormatGateComment(json, isWontDo: false);
        result.Should().NotBeNullOrWhiteSpace();
        result.Should().NotContain("```json");
    }

    // ── Return type is non-empty string ───────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FormatGateComment_AlwaysReturnsNonEmptyString(bool isWontDo)
    {
        var result = _sut.FormatGateComment(null, isWontDo);
        result.Should().NotBeNullOrEmpty();
    }
}
