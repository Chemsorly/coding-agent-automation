using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Spec 053 req 2.5: <see cref="AgentRouteReferences.EffortsItsToolRefuses"/> names the rows a profile's move to another
/// job template would leave with an effort the new tool refuses, for the route's effort and its light effort alike.
/// </summary>
public class AgentRouteReferencesTests
{
    private static readonly AgentProfile Claude = AgentRoutingTestData.Profile("profile-claude", "claude,dotnet", "Claude");
    private static readonly AgentProfile MovedToKiro = Claude with { JobTemplate = "dotnet,kiro" };

    private static readonly IReadOnlyDictionary<string, string> Tools = AgentRoutingTestData.ToolsByKey(
        ("dotnet,kiro", "kiro"), ("claude,dotnet", "claude"));

    private static IReadOnlyList<string> Refused(AgentRoute route, AgentProfile profile) =>
        AgentRouteReferences.EffortsItsToolRefuses(
            profile,
            AgentRoutingTestData.Routing("profile-kiro", "auto", "auto", (WorkItemTaskType.Decomposition, route)),
            [],
            Tools);

    [Fact]
    public void EffortsItsToolRefuses_RouteEffortTheToolRefuses_NamesTheRow()
    {
        var rows = Refused(AgentRoutingTestData.Route("profile-claude", "claude-opus-5-5", "xhigh"), MovedToKiro);

        rows.Should().ContainSingle().Which.Should().StartWith("Global · Decomposition: Kiro does not accept effort 'xhigh'");
    }

    [Fact]
    public void EffortsItsToolRefuses_LightEffortTheToolRefuses_NamesTheLightRow()
    {
        var route = AgentRoutingTestData.Route("profile-claude", "claude-opus-5-5", "high") with { LightEffort = "xhigh" };

        var rows = Refused(route, MovedToKiro);

        rows.Should().ContainSingle().Which.Should().StartWith("Global · Decomposition (light): Kiro does not accept effort 'xhigh'");
    }

    [Fact]
    public void EffortsItsToolRefuses_BlankLightEffort_IsNotARefusal()
    {
        var route = AgentRoutingTestData.Route("profile-claude", "claude-opus-5-5", "high") with { LightEffort = " " };

        Refused(route, MovedToKiro).Should().BeEmpty();
    }

    [Fact]
    public void EffortsItsToolRefuses_LightEffortTheToolAccepts_IsNotARefusal()
    {
        var route = AgentRoutingTestData.Route("profile-claude", "claude-opus-5-5", "high") with { LightEffort = "low" };

        Refused(route, MovedToKiro).Should().BeEmpty();
    }
}
