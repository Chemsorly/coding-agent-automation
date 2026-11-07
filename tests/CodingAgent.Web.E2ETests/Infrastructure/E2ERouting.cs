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

    /// <summary>
    /// Spec 056: dispatch routes every Pending item, so a test that dispatches a row it inserted routes the row's job
    /// template: one profile per template (a stable Id), and the global default route to it.
    /// </summary>
    public static Task SeedRoutedTemplateAsync(this E2EFixture fixture, string jobTemplate)
    {
        var id = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(jobTemplate)));
        return fixture.SeedRoutedProfileAsync(AgentRoutingTestData.Profile(id.ToString(), jobTemplate, $"E2E {jobTemplate}"));
    }

    /// <summary>
    /// Spec 056: routes the work of one repository to <paramref name="jobTemplate"/> without touching the global table, so
    /// rows routed to different templates can wait in one queue. Saves the template's profile and a (disabled) pipeline
    /// template that owns the returned repository provider id and routes Implementation work to the profile.
    /// </summary>
    public static async Task<string> SeedRepositoryRouteAsync(this E2EFixture fixture, string jobTemplate)
    {
        var key = string.Join("-", jobTemplate.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        var profileId = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(jobTemplate))).ToString();
        var repo = $"repo-e2e-{key}";
        await fixture.ConfigStore.SaveAgentProfileAsync(
            Converted(AgentRoutingTestData.Profile(profileId, jobTemplate, $"E2E {jobTemplate}")), CancellationToken.None);
        await fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = $"template-route-{key}",
            Name = $"E2E route {key}",
            IssueProviderId = "issue-e2e",
            RepoProviderId = repo,
            Enabled = false, // owns the repository for routing only; the loop never polls it
            AgentRoutes = new Dictionary<WorkItemTaskType, AgentRoute>
            {
                [WorkItemTaskType.Implementation] = new(profileId, Model, "auto"),
            },
        }, CancellationToken.None);
        return repo;
    }

    /// <summary>Replaces the global routing table.</summary>
    public static Task SeedRoutingAsync(this E2EFixture fixture, AgentRouting routing) =>
        AgentRoutingTestData.SeedRoutingAsync(fixture.DbContextFactory, routing);

    /// <summary>A profile as the Agent Profiles page saves it: tool settings present, even when empty.</summary>
    public static AgentProfile Converted(AgentProfile profile) =>
        profile.Settings is null ? profile with { Settings = new Dictionary<string, string>() } : profile;
}
