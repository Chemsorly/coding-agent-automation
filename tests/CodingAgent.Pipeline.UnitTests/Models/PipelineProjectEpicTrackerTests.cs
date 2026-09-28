using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for <see cref="PipelineProject.IsEpicTracker"/>, the rule that decides an epic's scope:
/// epics in the project's epic tracker are project epics, epics anywhere else are repo epics.
/// </summary>
public class PipelineProjectEpicTrackerTests
{
    private static PipelineProject Project(string? epicIssueProviderId) =>
        new() { Id = "p1", Name = "P1", EpicIssueProviderId = epicIssueProviderId };

    [Fact]
    public void IsEpicTracker_TheProjectsEpicTracker_ReturnsTrue()
    {
        Project("issue-epics").IsEpicTracker("issue-epics").Should().BeTrue();
    }

    [Fact]
    public void IsEpicTracker_AnotherTracker_ReturnsFalse()
    {
        Project("issue-epics").IsEpicTracker("issue-api").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsEpicTracker_ProjectWithoutEpicTracker_ReturnsFalse(string? epicIssueProviderId)
    {
        Project(epicIssueProviderId).IsEpicTracker("issue-api").Should().BeFalse();
        Project(epicIssueProviderId).IsEpicTracker(epicIssueProviderId).Should().BeFalse();
    }

    [Fact]
    public void IsEpicTracker_ComparesIdsExactly()
    {
        Project("Issue-Epics").IsEpicTracker("issue-epics").Should().BeFalse();
    }
}
