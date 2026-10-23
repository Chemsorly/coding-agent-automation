using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Web.TestUtilities;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Spec 053 task 5.8: the Routing page, repository rows on the Binding card, and a dispatch that runs with the route of
/// its task type. The harness's job templates are <c>kiro,dotnet</c> (Kiro) and <c>opencode,dotnet</c> (OpenCode),
/// among others.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class AgentRoutingE2ETests : E2ETestBase
{
    private const string KiroProfileId = "profile-routing-kiro";
    private const string OpenCodeProfileId = "profile-routing-opencode";
    private const string TemplateId = "template-routing-rows";
    private static readonly string[] EpicLabels = ["agent:epic"];

    public AgentRoutingE2ETests(E2EFixture fixture) : base(fixture) { }

    private AgentRoutingStore Routing => new(Fixture.DbContextFactory);

    private async Task SeedProfilesAsync()
    {
        await Fixture.ConfigStore.SaveAgentProfileAsync(
            AgentRoutingTestData.Profile(KiroProfileId, "kiro,dotnet", "Routing Kiro"), CancellationToken.None);
        await Fixture.ConfigStore.SaveAgentProfileAsync(
            AgentRoutingTestData.Profile(OpenCodeProfileId, "opencode,dotnet", "Routing OpenCode"), CancellationToken.None);
    }

    [Fact]
    public async Task RoutingPage_SavesTheDefaultAndATaskRow_AndRefusesARowWithoutModel()
    {
        await SeedProfilesAsync();
        var settingsPage = new SettingsPage(Page, BaseUrl);
        var routing = new AgentRoutingSectionHelper(Page);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Routing");
        await routing.WaitForTableAsync();

        Assert.Contains("Changes apply to work created after saving", await routing.GetAppliesNoteAsync());

        // Act: a default route and an OpenCode row for decomposition (OpenCode has no effort, so none is chosen).
        await routing.Table.SetRouteAsync("default", KiroProfileId, "auto", "high");
        await routing.Table.SetRouteAsync("decomposition", OpenCodeProfileId, "anthropic/claude-decomp");
        await routing.SaveAsync();

        var stored = await Routing.LoadAsync(CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(new AgentRoute(KiroProfileId, "auto", "high"), stored.Default);
        Assert.Equal(new AgentRoute(OpenCodeProfileId, "anthropic/claude-decomp", "auto"), stored.ByTaskType[WorkItemTaskType.Decomposition]);
        Assert.Contains("Routing Kiro", await routing.Table.GetInheritTextAsync("review"));

        // Act: a triage row without model is refused with its line, and nothing changes.
        await routing.Table.SetRouteAsync("triage", KiroProfileId, " ");
        var error = await routing.SaveExpectingErrorAsync();

        Assert.Contains("Triage: model is required", error);
        Assert.False((await Routing.LoadAsync(CancellationToken.None))!.ByTaskType.ContainsKey(WorkItemTaskType.Triage));
    }

    [Fact]
    public async Task AgentProfiles_AProfileARouteNames_CannotBeDeleted()
    {
        // Req 1.5: the refusal names the routes.
        await SeedProfilesAsync();
        await Fixture.SeedRoutingAsync(AgentRoutingTestData.Routing(KiroProfileId, E2ERouting.Model));
        var settingsPage = new SettingsPage(Page, BaseUrl);
        var profiles = new AgentProfileSectionHelper(Page);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Agent Profiles");

        await profiles.DeleteRowAsync("Routing Kiro");

        Assert.True(await profiles.IsStatusMessageVisibleAsync("cannot be deleted while routes name it: Global · Default"),
            "The refusal should name the global default route");
        Assert.Contains(await Fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None), p => p.Id == KiroProfileId);
    }

    [Fact]
    public async Task RepositoryRows_SetOnTheBindingCard_AreStoredOnTheTemplate()
    {
        await SeedProfilesAsync();
        await Fixture.SeedRoutingAsync(AgentRoutingTestData.Routing(KiroProfileId, E2ERouting.Model));
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = TemplateId,
            Name = "Routing Rows Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            ReviewEnabled = false,
        }, CancellationToken.None);
        var repositoryPage = new RepositoryPageObject(Page, BaseUrl);
        var table = new RouteTableHelper(Page);
        await repositoryPage.NavigateAsync(TemplateId);

        // Req 3.7: the review row is offered although the template's Review switch is off.
        Assert.Contains("global default", await table.GetInheritTextAsync("implementation"));
        await table.SetRouteAsync("review", OpenCodeProfileId, "anthropic/claude-review");
        await Page.ClickAsync("[data-testid='binding-routing-save']");
        await Page.Locator("[data-testid='binding-routing'] .status-success").WaitForAsync(new() { Timeout = 10_000 });

        var template = (await Fixture.ConfigStore.LoadAllTemplatesAsync(CancellationToken.None)).Single(t => t.Id == TemplateId);
        var row = Assert.Single(template.AgentRoutes!);
        Assert.Equal(WorkItemTaskType.Review, row.Key);
        Assert.Equal(new AgentRoute(OpenCodeProfileId, "anthropic/claude-review", "auto"), row.Value);
    }

    [Fact]
    public async Task DecompositionDispatch_RunsWithTheDecompositionRowsModel()
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "5301",
            Title = "Epic: routed decomposition",
            Description = "## Goal\nSplit this epic",
            Labels = EpicLabels
        });
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with { ClosedLoopPollInterval = TimeSpan.FromSeconds(1) }, CancellationToken.None);
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-routed-decomp",
            Name = "Routed Decomposition Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            DecompositionEnabled = true
        }, CancellationToken.None);
        await Fixture.ConfigStore.SaveAgentProfileAsync(
            AgentRoutingTestData.Profile("profile-routed-decomp", "e2e", "Routed Decomposition"), CancellationToken.None);
        // The default route has another model than the decomposition row; the agent must get the row's.
        await Fixture.SeedRoutingAsync(AgentRoutingTestData.Routing("profile-routed-decomp", E2ERouting.Model, "auto",
            (WorkItemTaskType.Decomposition, AgentRoutingTestData.Route("profile-routed-decomp", "decomposition-model"))));

        await using var fakeAgent = new FakeAgentClient("routed-decomp-agent", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        try
        {
            Assert.True(await Fixture.StartLoopAsync());

            var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(PipelineRunType.DecompositionAnalysis, assignment.RunType);
            var agentConfig = assignment.ProviderConfigs.Single(c => c.Id == assignment.AgentProviderConfigId);
            Assert.Equal("profile-routed-decomp", agentConfig.Id);
            Assert.Equal("decomposition-model", agentConfig.Settings[ProviderSettingKeys.Model]);
        }
        finally
        {
            await Fixture.StopLoopAsync();
        }
    }

    // ── Regression tests for issue #3852 — unkeyed option loops ──────────────

    private const string ClaudeCodeProfileId = "profile-routing-claude";

    private async Task SeedKiroAndClaudeProfilesAsync()
    {
        await Fixture.ConfigStore.SaveAgentProfileAsync(
            AgentRoutingTestData.Profile(KiroProfileId, "kiro,dotnet", "Routing Kiro"), CancellationToken.None);
        await Fixture.ConfigStore.SaveAgentProfileAsync(
            AgentRoutingTestData.Profile(ClaudeCodeProfileId, "claude,dotnet", "Routing Claude"), CancellationToken.None);
    }

    /// <summary>
    /// Regression for issue #3852: switching from a Kiro profile with effort <c>max</c> to a
    /// Claude Code profile (without touching effort) must keep <c>max</c> visible in the select.
    /// Before the fix, Blazor's positional diffing rewrote option[4] (Kiro's <c>max</c>) to
    /// Claude Code's <c>xhigh</c> and left it selected.
    /// </summary>
    [Fact]
    public async Task RoutingPage_KiroMaxToClaudeCode_EffortRemainsMax()
    {
        await SeedKiroAndClaudeProfilesAsync();
        await Fixture.SeedRoutingAsync(AgentRoutingTestData.Routing(KiroProfileId, "auto", "max"));

        var settingsPage = new SettingsPage(Page, BaseUrl);
        var routing = new AgentRoutingSectionHelper(Page);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Routing");
        await routing.WaitForTableAsync();

        // Switch profile only — ClaudeCode.Efforts includes 'max' so AfterProfileChanged keeps it.
        await routing.Table.SetRouteAsync("default", ClaudeCodeProfileId, "auto");

        // Use retry assertion (ToHaveValueAsync) to tolerate the Blazor Server SignalR round-trip
        // between SetRouteAsync returning and the re-render patching the effort option list.
        // A one-shot InputValueAsync read races the re-render and produces a false positive.
        await routing.Table.AssertSelectValueAsync("route-effort-default", "max");

        await routing.SaveAsync();
        var stored = await Routing.LoadAsync(CancellationToken.None);
        Assert.Equal("max", stored!.Default!.Effort);
    }

    /// <summary>
    /// Regression for issue #3852: switching from a Claude Code profile with effort <c>max</c>
    /// to a Kiro profile (without touching effort) must keep <c>max</c> visible in the select.
    /// Before the fix, removing Claude Code's extra <c>xhigh</c> option caused the browser to
    /// fall back to the first option (<c>auto</c>).
    /// </summary>
    [Fact]
    public async Task RoutingPage_ClaudeCodeMaxToKiro_EffortRemainsMax()
    {
        await SeedKiroAndClaudeProfilesAsync();
        await Fixture.SeedRoutingAsync(AgentRoutingTestData.Routing(ClaudeCodeProfileId, "auto", "max"));

        var settingsPage = new SettingsPage(Page, BaseUrl);
        var routing = new AgentRoutingSectionHelper(Page);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Routing");
        await routing.WaitForTableAsync();

        // Switch profile only — Kiro.Efforts includes 'max' so AfterProfileChanged keeps it.
        await routing.Table.SetRouteAsync("default", KiroProfileId, "auto");

        // Use retry assertion (ToHaveValueAsync) to tolerate the Blazor Server SignalR round-trip.
        await routing.Table.AssertSelectValueAsync("route-effort-default", "max");

        await routing.SaveAsync();
        var stored = await Routing.LoadAsync(CancellationToken.None);
        Assert.Equal("max", stored!.Default!.Effort);
    }

    /// <summary>
    /// Regression for issue #3852: switching a row from a disabled profile to an enabled profile
    /// that sorts after it must show the chosen profile in the select.
    /// Before the fix, Blazor's positional diffing shifted option positions when the disabled
    /// profile's option was removed, causing the next enabled profile to appear selected.
    /// </summary>
    [Fact]
    public async Task RoutingPage_LeavingDisabledProfile_ProfileSelectShowsChosenProfile()
    {
        const string DisabledId = "profile-repro-a-kiro";
        const string ProfileB = "profile-repro-b-kiro";
        const string ProfileC = "profile-repro-c-kiro";

        // Display names "Repro A (disabled)", "Repro B", "Repro C" sort alphabetically,
        // which matches the API's OrderBy(DisplayName) ordering → [A, B, C] in the select.
        await Fixture.ConfigStore.SaveAgentProfileAsync(
            AgentRoutingTestData.Profile(DisabledId, "kiro,dotnet", "Repro A (disabled)", enabled: false),
            CancellationToken.None);
        await Fixture.ConfigStore.SaveAgentProfileAsync(
            AgentRoutingTestData.Profile(ProfileB, "kiro,dotnet", "Repro B"), CancellationToken.None);
        await Fixture.ConfigStore.SaveAgentProfileAsync(
            AgentRoutingTestData.Profile(ProfileC, "kiro,dotnet", "Repro C"), CancellationToken.None);
        // Route to the disabled profile so OfferedProfiles includes it on the Default row.
        await Fixture.SeedRoutingAsync(AgentRoutingTestData.Routing(DisabledId, "auto", "auto"));

        var settingsPage = new SettingsPage(Page, BaseUrl);
        var routing = new AgentRoutingSectionHelper(Page);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Routing");
        await routing.WaitForTableAsync();

        // Switch to ProfileC; DisabledId is removed from OfferedProfiles (not enabled, not row.ProfileId).
        await routing.Table.SetRouteAsync("default", ProfileC, "auto");

        // Use retry assertion (ToHaveValueAsync) to tolerate the Blazor Server SignalR round-trip.
        // Without @key, Blazor removes the disabled-profile option after the re-render lands, shifting
        // option positions; a one-shot read before that re-render still sees ProfileC (just set by
        // SelectOptionAsync) and produces a false positive.
        await routing.Table.AssertSelectValueAsync("route-profile-default", ProfileC);

        await routing.SaveAsync();
        var stored = await Routing.LoadAsync(CancellationToken.None);
        Assert.Equal(ProfileC, stored!.Default!.ProfileId);
    }
}
