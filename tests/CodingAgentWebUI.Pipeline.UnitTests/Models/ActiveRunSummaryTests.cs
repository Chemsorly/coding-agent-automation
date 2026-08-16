using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Tests for ActiveRunSummary model.
/// </summary>
public class ActiveRunSummaryTests
{
    /// <summary>
    /// Type-locking test: asserts that IssueIdentifier is typed as IssueIdentifier (not string).
    /// Accessing .Value only compiles if the property is an IssueIdentifier struct — this test
    /// will fail to compile if the property type reverts to string.
    /// </summary>
    // TODO: The type-lock is only partially enforced — `string issueIdStr = summary.IssueIdentifier`
    // compiles whether the property is IssueIdentifier or string (implicit conversion goes both ways).
    // The real compile-time guard is `_ = summary.IssueIdentifier.Value`. Consider also adding edge
    // case assertions for default value and different identifier formats to improve coverage.
    [Fact]
    public void IssueIdentifier_IsTypedAsIssueIdentifier()
    {
        var summary = new ActiveRunSummary
        {
            RunId = "run-1",
            IssueIdentifier = "org/repo#100",
            IssueTitle = "Type lock test",
            RunType = PipelineRunType.Implementation,
            AgentId = null,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectName = null,
            CurrentStep = PipelineStep.GeneratingCode
        };

        // .Value is a member of IssueIdentifier struct — fails to compile if the property reverts to string
        string issueIdStr = summary.IssueIdentifier; // implicit conversion fires at assignment
        issueIdStr.Should().Be("org/repo#100");
        // Also access .Value to confirm compile-time type (would not compile if property were string)
        _ = summary.IssueIdentifier.Value;
    }
}
