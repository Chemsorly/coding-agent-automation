using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Spec 053: the API routes each work item by the global routing table (the <c>agent-routing</c> key-value row) and the
/// template's own rows. <see cref="E2EWebApplicationFactory.ResetAll"/> clears the table with the other key-value rows,
/// so a test that creates work seeds its profile and a route to it. The profile's job template is the selector the
/// fake job controller matches agents by.
/// </summary>
internal static class E2ERouting
{
    /// <summary>The model a route names by default: the one the former seeded agent provider config carried.</summary>
    public const string Model = "test-model";

    /// <summary>The job template key of an agent with these labels, as the Helm values of the harness list them.</summary>
    public static string Key(params IEnumerable<string> labels) => string.Join(",", labels);

    /// <summary>Saves <paramref name="profile"/> and routes every task type to it (the global default route).</summary>
    public static async Task SeedRoutedProfileAsync(this E2EFixture fixture, AgentProfile profile, string model = Model, string effort = "auto")
    {
        await fixture.ConfigStore.SaveAgentProfileAsync(Converted(profile), CancellationToken.None);
        await fixture.SeedRoutingAsync(AgentRoutingTestData.Routing(profile.Id, model, effort));
    }

    /// <summary>Replaces the global routing table.</summary>
    public static Task SeedRoutingAsync(this E2EFixture fixture, AgentRouting routing) =>
        AgentRoutingTestData.SeedRoutingAsync(fixture.DbContextFactory, routing);

    /// <summary>A profile as the Agent Profiles page saves it: tool settings present, even when empty.</summary>
    public static AgentProfile Converted(AgentProfile profile) =>
        profile.Settings is null ? profile with { Settings = new Dictionary<string, string>() } : profile;
}
