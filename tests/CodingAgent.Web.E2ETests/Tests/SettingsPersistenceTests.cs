using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests verifying that settings are persisted to the store and survive a page reload.
/// Covers the remaining pipeline sections not tested by <c>SettingsGlobalDefaultsTests</c>,
/// the agent-profile MCP server masking round-trip, and the Fetch Models widget.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class SettingsPersistenceTests : E2ETestBase
{
    public SettingsPersistenceTests(E2EFixture fixture) : base(fixture) { }

    // ── Shared helpers ────────────────────────────────────────────────────

    private SettingsPage Settings => new(Page, BaseUrl);

    // ── Theory: pipeline sections ─────────────────────────────────────────

    /// <summary>
    /// Fills a numeric setting on the specified pipeline-section page, saves, asserts the store
    /// holds the new value, reloads, and confirms the input shows the same value.
    ///
    /// Mutation-check target for <c>MaxDecompositionSubIssues</c>: removing that field from the
    /// <c>with</c> clause in <c>PipelineDecompositionSection.razor</c> causes both the store
    /// assertion and the reload assertion to fail.
    /// </summary>
    [Theory]
    [InlineData(SettingsNodes.PipelineLoop, "ClosedLoopMaxRunsPerCycle", "17", "Save Pipeline Loop Settings", "Pipeline loop settings saved.")]
    [InlineData(SettingsNodes.PipelineCi, "CiNotStartedMaxRetries", "7", "Save External CI Settings", "External CI settings saved.")]
    [InlineData(SettingsNodes.PipelineCodeReview, "CodeReview.InlineComments.MaxInlineComments", "11", "Save Code Review Settings", "Code review settings saved.")]
    [InlineData(SettingsNodes.PipelineDecomposition, "MaxDecompositionSubIssues", "13", "Save Decomposition Settings", "Decomposition settings saved.")]
    [InlineData(SettingsNodes.PipelineConsolidation, "MaxRefactoringProposals", "8", "Save Consolidation Settings", "Consolidation settings saved.")]
    [InlineData(SettingsNodes.PipelineTriage, "TriageRetentionDays", "21", "Save Triage Settings", "Triage settings saved.")]
    public async Task Section_ValueSurvivesSaveAndReload(
        string section,
        string setting,
        string value,
        string saveButton,
        string status)
    {
        // ── Navigate ───────────────────────────────────────────────────────
        await Page.GotoCockpitPageAsync($"{BaseUrl}/settings?section={section}");
        await Settings.ExpandAdvancedSectionsAsync();

        // ── Fill the numeric input ─────────────────────────────────────────
        var input = Page.Locator($"[data-setting='{setting}'] input[type='number']");
        await input.FillAsync(value);

        // ── Save ──────────────────────────────────────────────────────────
        await Page.Locator($"button:has-text('{saveButton}')").ClickAsync();

        // Assert: success toast appears (auto-dismissed after 4 s; assert immediately after click)
        await Assertions.Expect(Page.Locator(".inline-status-success, div.settings-status.status-success").First)
            .ToContainTextAsync(status, new LocatorAssertionsToContainTextOptions { Timeout = 5_000 });

        // ── Assert store ──────────────────────────────────────────────────
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        var stored = setting switch
        {
            "ClosedLoopMaxRunsPerCycle" => config.ClosedLoopMaxRunsPerCycle.ToString(),
            "CiNotStartedMaxRetries" => config.CiNotStartedMaxRetries.ToString(),
            "CodeReview.InlineComments.MaxInlineComments" => config.CodeReview.InlineComments.MaxInlineComments.ToString(),
            "MaxDecompositionSubIssues" => config.MaxDecompositionSubIssues.ToString(),
            "MaxRefactoringProposals" => config.MaxRefactoringProposals.ToString(),
            "TriageRetentionDays" => config.TriageRetentionDays.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Unmapped setting")
        };
        Assert.Equal(value, stored);

        // ── Reload and assert input ───────────────────────────────────────
        // ReloadCockpitPageAsync keeps the query string, so the section remains selected
        await Page.ReloadCockpitPageAsync();
        await Settings.ExpandAdvancedSectionsAsync();
        await Assertions.Expect(Page.Locator($"[data-setting='{setting}'] input[type='number']"))
            .ToHaveValueAsync(value);
    }

    // ── Facts ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills the Analysis Prompt textarea, saves, checks the store, reloads, confirms the textarea value.
    /// </summary>
    [Fact]
    public async Task Prompts_TextSurvivesSaveAndReload()
    {
        const string promptText = "U05 analysis prompt";

        await Page.GotoCockpitPageAsync($"{BaseUrl}/settings?section={SettingsNodes.PipelinePrompts}");
        await Page.Locator("[data-setting='AnalysisPrompt'] textarea").FillAsync(promptText);
        await Page.Locator("button:has-text('Save Prompt Settings')").ClickAsync();

        await Assertions.Expect(Page.Locator(".inline-status-success, div.settings-status.status-success").First)
            .ToContainTextAsync("Prompt settings saved.", new LocatorAssertionsToContainTextOptions { Timeout = 5_000 });

        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        Assert.Equal(promptText, config.AnalysisPrompt);

        await Page.ReloadCockpitPageAsync();
        await Assertions.Expect(Page.Locator("[data-setting='AnalysisPrompt'] textarea"))
            .ToHaveValueAsync(promptText);
    }

    /// <summary>
    /// Reads the current state of the Triage Review checkbox, toggles it, saves, asserts the store
    /// reflects the toggle, reloads, and confirms the checkbox matches.
    /// </summary>
    [Fact]
    public async Task Triage_ReviewCheckbox_SurvivesSaveAndReload()
    {
        await Page.GotoCockpitPageAsync($"{BaseUrl}/settings?section={SettingsNodes.PipelineTriage}");

        // Read the current stored value so the test is state-independent
        var configBefore = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        var before = configBefore.TriageReviewEnabled;

        // Toggle the checkbox
        await Page.Locator("[data-setting='TriageReviewEnabled'] input[type='checkbox']").ClickAsync();

        await Page.Locator("button:has-text('Save Triage Settings')").ClickAsync();

        await Assertions.Expect(Page.Locator(".inline-status-success, div.settings-status.status-success").First)
            .ToContainTextAsync("Triage settings saved.", new LocatorAssertionsToContainTextOptions { Timeout = 5_000 });

        // Assert store reflects the toggle
        var configAfter = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        Assert.Equal(!before, configAfter.TriageReviewEnabled);

        // Assert persists after reload
        await Page.ReloadCockpitPageAsync();
        var checkboxLocator = Page.Locator("[data-setting='TriageReviewEnabled'] input[type='checkbox']");
        var reloadedValue = await checkboxLocator.IsCheckedAsync();
        Assert.Equal(!before, reloadedValue);
    }

    /// <summary>
    /// Adds an HTTP MCP server to an agent profile, verifies it is stored, confirms the headers are
    /// masked on reload, and verifies resaving with the masked value keeps the original secret.
    /// </summary>
    [Fact]
    public async Task ProfileMcpServer_IsStored_AndMaskedOnReload_AndKeptOnResave()
    {
        // ── Arrange: seed a routed profile ───────────────────────────────
        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "MCP U05",
            JobTemplate = E2ERouting.Key("e2e"),
            Enabled = true,
        });

        var helper = new AgentProfileSectionHelper(Page);

        // ── Navigate to Agent Profiles and edit the profile ──────────────
        await Page.GotoCockpitPageAsync($"{BaseUrl}/settings?section={SettingsNodes.AgentProfiles}");
        // Wait for the profile table to populate before attempting to click the row's Edit button.
        // GotoCockpitPageAsync only waits for navigation readiness; the section's data fetch may
        // not have completed yet when navigating via query-string rather than via SelectTreeNodeAsync.
        await Assertions.Expect(Page.Locator("tr[data-testid^='profile-row-']")).ToBeVisibleAsync();
        await helper.EditRowAsync("MCP U05");

        // ── Open the MCP server sub-form ─────────────────────────────────
        // The btn-add inside the profile form is the "+ Add MCP Server" button
        await Page.Locator("div.provider-form").First.Locator("button.btn-add").ClickAsync();

        // Wait for the inner MCP form to appear
        await Assertions.Expect(Page.Locator("div.provider-form div.provider-form")).ToBeVisibleAsync();

        // ── Fill MCP server fields ────────────────────────────────────────
        // Name input: placeholder "e.g. context7"
        // @bind uses default onchange; BlurAsync fires the event before focus moves to the Type select.
        await Page.Locator("input[placeholder='e.g. context7']").FillAsync("u05-http");
        await Page.Locator("input[placeholder='e.g. context7']").BlurAsync();

        // Type select: inner form's select (profile form has [data-testid='profile-job-template'])
        await Page.Locator("div.provider-form div.provider-form select").SelectOptionAsync("http");

        // URL input: placeholder "e.g. https://mcp.context7.com/mcp" (rendered when type is "http")
        // @bind uses default onchange; BlurAsync fires the event before filling the Headers textarea.
        await Assertions.Expect(Page.Locator("input[placeholder='e.g. https://mcp.context7.com/mcp']")).ToBeVisibleAsync();
        await Page.Locator("input[placeholder='e.g. https://mcp.context7.com/mcp']").FillAsync("https://mcp.example.test/mcp");
        await Page.Locator("input[placeholder='e.g. https://mcp.context7.com/mcp']").BlurAsync();

        // Headers textarea (only present for "http" type, uses @bind:event="oninput")
        await Page.Locator("div.provider-form div.provider-form textarea").FillAsync("Authorization=Bearer u05-token");

        // ── Save the MCP server (closes the MCP sub-form) ─────────────────
        await Page.Locator("button.btn-save:has-text('Save Server')").ClickAsync();

        // Wait for the MCP sub-form to close (the row should now appear in the table)
        await Assertions.Expect(Page.Locator("div.provider-form div.provider-form")).ToHaveCountAsync(0);

        // ── Save the profile ──────────────────────────────────────────────
        await helper.SaveAsync();

        // ── Assert: store has the MCP server with the header ─────────────
        var profiles = await Fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None);
        var profile = profiles.Single(p => p.DisplayName == "MCP U05");
        Assert.Single(profile.McpServers);
        var server = profile.McpServers[0];
        Assert.Equal("u05-http", server.Name);
        Assert.Equal("https://mcp.example.test/mcp", server.Url);
        Assert.Equal("Bearer u05-token", server.Headers["Authorization"]);

        // ── Reload and confirm header is masked ───────────────────────────
        await Page.ReloadCockpitPageAsync();
        await helper.EditRowAsync("MCP U05");

        // Find the MCP server row and click Edit
        await Page.Locator("tr:has-text('u05-http') button.btn-edit").ClickAsync();

        // Wait for the MCP form to open
        await Assertions.Expect(Page.Locator("div.provider-form div.provider-form")).ToBeVisibleAsync();

        // The masked header value is "Authorization=****" (UiSecretMask.Redact masks header values)
        await Assertions.Expect(Page.Locator("div.provider-form div.provider-form textarea"))
            .ToHaveValueAsync("Authorization=****");

        // ── Resave with the masked value; the API restores the original ───
        await Page.Locator("button.btn-save:has-text('Save Server')").ClickAsync();
        await Assertions.Expect(Page.Locator("div.provider-form div.provider-form")).ToHaveCountAsync(0);
        await helper.SaveAsync();

        // ── Assert: original secret is still in the store ─────────────────
        var profilesAfter = await Fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None);
        var serverAfter = profilesAfter.Single(p => p.DisplayName == "MCP U05").McpServers[0];
        Assert.Equal("Bearer u05-token", serverAfter.Headers["Authorization"]);
    }

    /// <summary>
    /// Stubs the models endpoint with a list that includes "auto" and two real models.
    /// Verifies the count shown is 2 (auto is excluded from the count).
    /// </summary>
    [Fact]
    public async Task FetchModels_ApiAnswers_ShowsTheCount()
    {
        // Route before navigating
        await Page.RouteAsync("**/api/ui/settings/models", async route =>
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = """[{"modelId":"auto"},{"modelId":"model-a"},{"modelId":"model-b"}]""",
            });
        });

        await Page.GotoCockpitPageAsync($"{BaseUrl}/settings?section={SettingsNodes.AgentRouting}");
        await Page.Locator("[data-testid='routing-fetch-models']").ClickAsync();

        // AgentRoutingSection filters out "auto" from the count
        await Assertions.Expect(Page.Locator(".inline-status-success"))
            .ToContainTextAsync("2 model(s) loaded.");
    }

    /// <summary>
    /// Stubs the models endpoint with a 502 and a JSON string body.
    /// Verifies the error reason is shown and the Fetch Models button is visible again.
    /// </summary>
    [Fact]
    public async Task FetchModels_ApiRefuses_ShowsTheReason()
    {
        // Route before navigating
        await Page.RouteAsync("**/api/ui/settings/models", async route =>
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 502,
                ContentType = "application/json",
                Body = "\"No agent image for this tool\"",   // JSON string — UiApiClient unwraps it
            });
        });

        await Page.GotoCockpitPageAsync($"{BaseUrl}/settings?section={SettingsNodes.AgentRouting}");
        await Page.Locator("[data-testid='routing-fetch-models']").ClickAsync();

        // UiProviderClient.GetModelsAsync catches the InvalidOperationException and returns the reason;
        // AgentRoutingSection fires OnShowStatus with IsError=true
        await Assertions.Expect(Page.Locator(".inline-status-error"))
            .ToContainTextAsync("No agent image for this tool");

        // After an error the button is re-enabled
        await Assertions.Expect(Page.Locator("[data-testid='routing-fetch-models']")).ToBeVisibleAsync();
    }
}
