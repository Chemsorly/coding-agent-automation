using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Unit tests for <see cref="GateCommentFormatter"/>, verifying all formatting branches:
/// null/empty JSON, valid JSON delegation to <see cref="AgentPhaseExecutor"/>, and
/// invalid JSON fallback to code block wrapping.
/// </summary>
public sealed class GateCommentFormatterTests
{
    private readonly GateCommentFormatter _formatter = new(Mock.Of<ILogger>());

    #region Null/empty JSON — header only

    [Fact]
    public void FormatGateComment_NullJson_WontDo_ReturnsWontDoHeader()
    {
        var result = _formatter.FormatGateComment(null, isWontDo: true);

        result.Should().Be("## 🚫 Analysis Gate: Won't Do");
    }

    [Fact]
    public void FormatGateComment_NullJson_NotReady_ReturnsNeedsRefinementHeader()
    {
        var result = _formatter.FormatGateComment(null, isWontDo: false);

        result.Should().Be("## ⚠️ Analysis Gate: Needs Refinement");
    }

    [Fact]
    public void FormatGateComment_EmptyJson_WontDo_ReturnsWontDoHeader()
    {
        var result = _formatter.FormatGateComment("   ", isWontDo: true);

        result.Should().Be("## 🚫 Analysis Gate: Won't Do");
    }

    [Fact]
    public void FormatGateComment_EmptyJson_NotReady_ReturnsNeedsRefinementHeader()
    {
        var result = _formatter.FormatGateComment("", isWontDo: false);

        result.Should().Be("## ⚠️ Analysis Gate: Needs Refinement");
    }

    #endregion

    #region Valid JSON — delegates to AgentPhaseExecutor static methods

    [Fact]
    public void FormatGateComment_ValidJson_WontDo_DelegatesToExecutor()
    {
        var assessment = new AnalysisAssessment
        {
            Recommendation = "wont_do",
            Reason = "No code changes needed",
            Concerns = new[] { "This is a documentation-only issue" }
        };
        var json = JsonSerializer.Serialize(assessment);

        var result = _formatter.FormatGateComment(json, isWontDo: true);

        // Should match AgentPhaseExecutor.BuildWontDoComment output
        var expected = AgentPhaseExecutor.BuildWontDoComment(assessment);
        result.Should().Be(expected);
    }

    [Fact]
    public void FormatGateComment_ValidJson_NotReady_DelegatesToExecutor()
    {
        var assessment = new AnalysisAssessment
        {
            Recommendation = "not_ready",
            Reason = "Issue is too vague",
            Concerns = new[] { "Missing acceptance criteria" },
            BlockingIssues = new[] { "No repository access" }
        };
        var json = JsonSerializer.Serialize(assessment);

        var result = _formatter.FormatGateComment(json, isWontDo: false);

        // Should match AgentPhaseExecutor.BuildNotReadyComment output
        var expected = AgentPhaseExecutor.BuildNotReadyComment(assessment);
        result.Should().Be(expected);
    }

    #endregion

    #region Invalid JSON — wraps in code block

    [Fact]
    public void FormatGateComment_InvalidJson_WontDo_WrapsInCodeBlock()
    {
        var invalidJson = "{ this is not valid json }}}";

        var result = _formatter.FormatGateComment(invalidJson, isWontDo: true);

        result.Should().Contain("## 🚫 Analysis Gate: Won't Do");
        result.Should().Contain("```json");
        result.Should().Contain(invalidJson);
    }

    [Fact]
    public void FormatGateComment_InvalidJson_NotReady_WrapsInCodeBlock()
    {
        var invalidJson = "not json at all";

        var result = _formatter.FormatGateComment(invalidJson, isWontDo: false);

        result.Should().Contain("## ⚠️ Analysis Gate: Needs Refinement");
        result.Should().Contain("```json");
        result.Should().Contain(invalidJson);
    }

    #endregion
}
