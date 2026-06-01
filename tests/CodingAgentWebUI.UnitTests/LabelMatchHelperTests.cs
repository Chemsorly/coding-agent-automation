using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="LabelMatchHelper"/>.
/// </summary>
public class LabelMatchHelperTests
{
    [Fact]
    public void IsLabelMatch_EmptyRequiredLabels_ReturnsTrue()
    {
        var result = LabelMatchHelper.IsLabelMatch(["dotnet", "agent"], []);
        result.Should().BeTrue();
    }

    [Fact]
    public void IsLabelMatch_AgentHasAllRequired_ReturnsTrue()
    {
        var result = LabelMatchHelper.IsLabelMatch(["dotnet", "linux", "agent"], ["dotnet", "linux"]);
        result.Should().BeTrue();
    }

    [Fact]
    public void IsLabelMatch_AgentMissingRequired_ReturnsFalse()
    {
        var result = LabelMatchHelper.IsLabelMatch(["python", "agent"], ["dotnet"]);
        result.Should().BeFalse();
    }

    [Fact]
    public void IsLabelMatch_CaseInsensitive_ReturnsTrue()
    {
        var result = LabelMatchHelper.IsLabelMatch(["DotNet", "Agent"], ["dotnet", "agent"]);
        result.Should().BeTrue();
    }
}
