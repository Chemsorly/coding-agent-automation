using CodingAgent.Pipeline;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the agent profile and quality settings CRUD and the template label preview.
///
/// Covers four scenarios (issue #3100, agent profiles as spec 053 has them):
/// 1. Agent Profile CRUD (add on a job template, edit a tool setting, disable, delete)
/// 2. Quality Gate Config CRUD (add, edit command, disable, delete)
/// 3. Reviewer Config CRUD and Reset-to-Defaults
/// 4. Label preview on Pipelines (QG appears after add, disappears after disable)
///
/// Routing is covered by AgentRoutingE2ETests and, headless, by DbModeAgentLifecycleTests.
/// These tests focus exclusively on the UI CRUD flows.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class LabelRoutingSettingsCrudTests : E2ETestBase
{
    public LabelRoutingSettingsCrudTests(E2EFixture fixture) : base(fixture) { }

    // ── Scenario 1: Agent Profile CRUD ────────────────────────────────────

    /// <summary>
    /// Spec 053 req 1.1–1.5: add a profile on a Helm job template (the form shows that template's tool settings) →
    /// edit its tool setting → disable → delete. The table and status toasts reflect each step, including after a
    /// reload; the store holds the job template and the tool settings and none of the retired label fields.
    /// </summary>
    [Fact]
    public async Task AgentProfile_AddEditDisableDelete()
    {
        // Arrange
        var settingsPage = new SettingsPage(Page, BaseUrl);
        var profiles = new AgentProfileSectionHelper(Page);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Agent Profiles");

        // Assert: no profiles exist at start
        Assert.Empty(await profiles.GetTableRowNamesAsync());

        // Act: add a profile on the Kiro job template; the Kiro tool's Agent Name field appears
        await profiles.ClickAddAsync();
        await profiles.FillDisplayNameAsync("E2E Test Profile");
        await profiles.SelectJobTemplateAsync("dotnet,kiro");
        Assert.True(await profiles.IsAgentNameShownAsync(), "A Kiro job template shows the Kiro tool's Agent Name setting");
        await profiles.FillAgentNameAsync("e2e-agent");
        await profiles.SetEnabledAsync(true);
        await profiles.SaveAsync();

        // Assert: row appears with its job template and tool, save toast shown
        Assert.Contains("E2E Test Profile", await profiles.GetTableRowNamesAsync());
        Assert.Contains("Kiro", await profiles.GetJobTemplateCellAsync("E2E Test Profile"));
        Assert.True(await profiles.IsStatusMessageVisibleAsync("saved"), "Expected 'saved' status message after add");
        var stored = (await Fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None)).Single(p => p.DisplayName == "E2E Test Profile");
        Assert.Equal("dotnet,kiro", stored.JobTemplate);
        Assert.Equal("e2e-agent", stored.Settings![ProviderSettingKeys.AgentName]);
        Assert.Empty(stored.MatchLabels);
        Assert.Equal("", stored.AgentProviderConfigId);

        // Assert: persists after reload
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Agent Profiles");
        Assert.Contains("E2E Test Profile", await profiles.GetTableRowNamesAsync());

        // Act: edit the tool setting
        await profiles.EditRowAsync("E2E Test Profile");
        await profiles.FillAgentNameAsync("e2e-reviewer");
        await profiles.SaveAsync();

        // Assert: saved and still enabled
        Assert.True(await profiles.IsStatusMessageVisibleAsync("saved"), "Expected 'saved' status message after edit");
        Assert.True(await profiles.IsRowEnabledAsync("E2E Test Profile"), "Profile should remain enabled after the edit");
        var storedAfterEdit = (await Fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None)).Single(p => p.DisplayName == "E2E Test Profile");
        Assert.Equal("e2e-reviewer", storedAfterEdit.Settings![ProviderSettingKeys.AgentName]);

        // Act: disable the profile (no route names it, so that is allowed)
        await profiles.EditRowAsync("E2E Test Profile");
        await profiles.SetEnabledAsync(false);
        await profiles.SaveAsync();

        // Assert: row shows the disabled icon, also after a reload
        Assert.False(await profiles.IsRowEnabledAsync("E2E Test Profile"), "Profile row should show disabled icon after disabling");
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Agent Profiles");
        Assert.False(await profiles.IsRowEnabledAsync("E2E Test Profile"), "Profile row should still show disabled icon after reload");

        // Act: delete the profile
        await profiles.DeleteRowAsync("E2E Test Profile");

        // Assert: row removed, delete toast shown, store no longer has it
        Assert.DoesNotContain("E2E Test Profile", await profiles.GetTableRowNamesAsync());
        Assert.True(await profiles.IsStatusMessageVisibleAsync("deleted"), "Expected 'deleted' status message after delete");
        Assert.DoesNotContain(await Fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None), p => p.DisplayName == "E2E Test Profile");
    }

    // ── Scenario 2: Quality Gate Config CRUD ─────────────────────────────

    /// <summary>
    /// Verifies the full Quality Gate Config CRUD lifecycle:
    /// add a config → edit command → disable → delete.
    /// The pre-seeded "E2E Quality Gate" remains throughout.
    /// </summary>
    [Fact]
    public async Task QualityGateConfig_AddEditDisableDelete()
    {
        // Arrange
        var settingsPage = new SettingsPage(Page, BaseUrl);
        var qgHelper = new QualityGateConfigSectionHelper(Page);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Quality Gate Configs");

        // Assert: pre-seeded "E2E Quality Gate" is present
        var initialNames = await qgHelper.GetTableRowNamesAsync();
        Assert.Contains("E2E Quality Gate", initialNames.Select(n => n.Replace("GLOBAL", "").Trim()));

        // Act: add a new config
        await qgHelper.ClickAddAsync();
        await qgHelper.FillDisplayNameAsync("Dotnet Gate");
        await qgHelper.FillMatchLabelsAsync("dotnet");
        await qgHelper.FillCompilationCommandAsync("dotnet build");
        await qgHelper.FillTestCommandAsync("dotnet test");
        await qgHelper.SaveAsync();

        // Assert: row appears, save toast shown
        var namesAfterAdd = await qgHelper.GetTableRowNamesAsync();
        Assert.Contains("Dotnet Gate", namesAfterAdd.Select(n => n.Replace("GLOBAL", "").Trim()));
        Assert.True(await qgHelper.IsStatusMessageVisibleAsync("saved"), "Expected 'saved' status after add");

        // Assert: persists after reload (issue criterion: "table reflects each step after reload")
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Quality Gate Configs");
        var namesAfterAddReload = await qgHelper.GetTableRowNamesAsync();
        Assert.Contains("Dotnet Gate", namesAfterAddReload.Select(n => n.Replace("GLOBAL", "").Trim()));

        // Act: edit the compilation command
        await qgHelper.EditRowAsync("Dotnet Gate");
        await qgHelper.FillCompilationCommandAsync("dotnet build -c Release");
        await qgHelper.SaveAsync();

        // Assert: save toast
        Assert.True(await qgHelper.IsStatusMessageVisibleAsync("saved"), "Expected 'saved' status after edit");

        // Assert via store: updated command persisted
        var stored = await Fixture.ConfigStore.LoadQualityGateConfigsAsync(CancellationToken.None);
        var dotnetGate = stored.FirstOrDefault(c => c.DisplayName == "Dotnet Gate");
        Assert.NotNull(dotnetGate);
        Assert.Equal("dotnet build -c Release", dotnetGate.CompilationCommand);

        // TODO [WARNING]: Scenario 2 requires "The assignment for a dotnet job contains the edited commands.
        // A disabled config is not included." The test verifies the edited command in the store, and verifies
        // the disabled UI icon, but does not call QualityGateResolver.Resolve to confirm (a) the edited commands
        // appear in a resolved assignment and (b) the disabled config is excluded from the resolved assignment.

        // Act: disable the config
        await qgHelper.EditRowAsync("Dotnet Gate");
        await qgHelper.SetEnabledAsync(false);
        await qgHelper.SaveAsync();

        // Assert: row shows disabled icon
        Assert.False(await qgHelper.IsRowEnabledAsync("Dotnet Gate"), "Config row should show disabled icon after disabling");

        // Assert: disabled state persists after reload
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Quality Gate Configs");
        Assert.False(await qgHelper.IsRowEnabledAsync("Dotnet Gate"), "Config row should still show disabled icon after reload");

        // Act: delete the config
        await qgHelper.DeleteRowAsync("Dotnet Gate");

        // Assert: row removed; pre-seeded QG still present
        var namesAfterDelete = await qgHelper.GetTableRowNamesAsync();
        Assert.DoesNotContain("Dotnet Gate", namesAfterDelete.Select(n => n.Replace("GLOBAL", "").Trim()));
        Assert.Contains("E2E Quality Gate", namesAfterDelete.Select(n => n.Replace("GLOBAL", "").Trim()));
    }

    // ── Scenario 3: Reviewer Config CRUD and Reset-to-Defaults ───────────

    /// <summary>
    /// Verifies the full Reviewer Config CRUD lifecycle and the reset-to-defaults feature:
    /// add a config (with 2 agents) → edit agent prompt → delete → reset to defaults.
    /// </summary>
    [Fact]
    public async Task ReviewerConfig_AddEditDeleteAndReset()
    {
        // Arrange
        var settingsPage = new SettingsPage(Page, BaseUrl);
        var reviewerHelper = new ReviewerConfigSectionHelper(Page);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Reviewer Configs");

        // Assert: no reviewer configs exist at start
        var initialNames = await reviewerHelper.GetTableRowNamesAsync();
        Assert.Empty(initialNames);

        // Act: add a reviewer config
        // ShowAddForm() pre-populates one empty agent card at index 0 — fill it directly.
        await reviewerHelper.ClickAddAsync();
        await reviewerHelper.FillAgentNameAsync(0, "Agent1");
        await reviewerHelper.FillAgentPromptAsync(0, "Review prompt 1");
        // Now add a second agent card
        await reviewerHelper.ClickAddAgentAsync();
        await reviewerHelper.FillAgentNameAsync(1, "Agent2");
        await reviewerHelper.FillAgentPromptAsync(1, "Review prompt 2");
        await reviewerHelper.FillDisplayNameAsync("E2E Reviewers");
        await reviewerHelper.SaveAsync();

        // Assert: row appears, save toast shown
        var namesAfterAdd = await reviewerHelper.GetTableRowNamesAsync();
        Assert.Contains("E2E Reviewers", namesAfterAdd.Select(n => n.Replace("GLOBAL", "").Trim()));
        Assert.True(await reviewerHelper.IsStatusMessageVisibleAsync("saved"), "Expected 'saved' status after add");

        // Assert: persists after reload (issue criterion: "table reflects each step after reload")
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Reviewer Configs");
        var namesAfterAddReload = await reviewerHelper.GetTableRowNamesAsync();
        Assert.Contains("E2E Reviewers", namesAfterAddReload.Select(n => n.Replace("GLOBAL", "").Trim()));

        // Act: edit Agent2's prompt
        await reviewerHelper.EditRowAsync("E2E Reviewers");
        await reviewerHelper.FillAgentPromptAsync(1, "Updated review prompt 2");
        await reviewerHelper.SaveAsync();

        // Assert: save toast
        Assert.True(await reviewerHelper.IsStatusMessageVisibleAsync("saved"), "Expected 'saved' status after edit");

        // Assert via store: updated prompt persisted
        var stored = await Fixture.ConfigStore.LoadReviewerConfigsAsync(CancellationToken.None);
        var e2eReviewer = stored.FirstOrDefault(c => c.DisplayName == "E2E Reviewers");
        Assert.NotNull(e2eReviewer);
        var agent2 = e2eReviewer.Agents.FirstOrDefault(a => a.Name == "Agent2");
        Assert.NotNull(agent2);
        Assert.Equal("Updated review prompt 2", agent2.Prompt);

        // Act: delete the config
        await reviewerHelper.DeleteRowAsync("E2E Reviewers");

        // Assert: row removed from UI
        var namesAfterDelete = await reviewerHelper.GetTableRowNamesAsync();
        Assert.DoesNotContain("E2E Reviewers", namesAfterDelete.Select(n => n.Replace("GLOBAL", "").Trim()));

        // TODO [WARNING]: Verify via store that the delete actually removed the record before calling reset,
        // to isolate a silent delete failure from a subsequent reset that would mask it:
        // var storedAfterDelete = await Fixture.ConfigStore.LoadReviewerConfigsAsync(CancellationToken.None);
        // Assert.DoesNotContain(storedAfterDelete, c => c.DisplayName == "E2E Reviewers");

        // Act: reset to defaults
        await reviewerHelper.ClickResetToDefaultsAsync();
        await reviewerHelper.ConfirmResetAsync();

        // Assert: toast confirms reset
        Assert.True(await reviewerHelper.IsStatusMessageVisibleAsync("reset to defaults"), "Expected 'reset to defaults' status after reset");

        // Assert: both default rows appear in the table
        var namesAfterReset = (await reviewerHelper.GetTableRowNamesAsync())
            .Select(n => n.Replace("GLOBAL", "").Trim())
            .ToList();
        Assert.Contains("Default Reviewers", namesAfterReset);
        Assert.Contains(".NET Reviewers", namesAfterReset);

        // Assert via store: the stack-agnostic reviewers for every repository, and the .NET specialist for dotnet repositories
        var storedAfterReset = await Fixture.ConfigStore.LoadReviewerConfigsAsync(CancellationToken.None);
        Assert.Equal(2, storedAfterReset.Count);
        var defaults = Assert.Single(storedAfterReset, c => c.DisplayName == "Default Reviewers");
        Assert.Empty(defaults.MatchLabels);
        Assert.Equal(["Correctness", "SecurityReviewer", "TestQualityReviewer"], defaults.Agents.Select(a => a.Name));
        var dotNet = Assert.Single(storedAfterReset, c => c.DisplayName == ".NET Reviewers");
        Assert.Equal(["dotnet"], dotNet.MatchLabels);
        Assert.Equal(["DotNetSpecialist"], dotNet.Agents.Select(a => a.Name));
    }

    // ── Scenario 4: Label Preview on Pipelines ────────────────────────────

    /// <summary>
    /// Verifies the label preview on the template table:
    /// - With the template's stack label "dotnet" and a labelled QG config added, the preview
    ///   lists the QG config.
    /// - After disabling the QG config, the preview no longer lists it.
    /// - The pre-seeded global "E2E Quality Gate" (empty MatchLabels) keeps the button visible
    ///   throughout; "button absent" is never the expected state in this test.
    ///
    /// Setup: the template carries the stack label "dotnet"; the seeded "E2E Repo Provider" is reused.
    /// </summary>
    [Fact]
    public async Task TemplateLabelPreview_ShowsAndHidesWithQualityGateEnabled()
    {
        // TODO [WARNING]: This test mutates shared fixture state (the template) without restoring it in a teardown
        // step. If test ordering causes this test to run before other tests that read the template table, those tests
        // may see unexpected data. Consider a try/finally block or DisposeAsync override.

        // TODO [WARNING]: CancellationToken.None is passed to all ConfigStore calls. Thread the xUnit
        // cancellation token through (TestContext.Current.CancellationToken in xUnit v3) so teardown is not blocked.

        // Arrange: seed a template that uses the default repo provider
        const string templateName = "Label Preview Template";
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-label-preview",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            // Stack labels, so the row offers a label preview (spec 053: no global default labels any more).
            RequiredLabels = ["dotnet"]
        }, CancellationToken.None);


        var settingsPage = new SettingsPage(Page, BaseUrl);
        var qgHelper = new QualityGateConfigSectionHelper(Page);
        var templateHelper = new TemplateTableSectionHelper(Page, BaseUrl);

        // Step 1: Navigate to /agent-coding and confirm the preview button is visible.
        // The pre-seeded "E2E Quality Gate" (empty MatchLabels = global) already matches "dotnet".
        await templateHelper.NavigateAsync();
        // TODO [WARNING]: NavigateAsync uses a fixed 3s delay; use WaitForLabelPreviewButtonAsync for determinism
        Assert.True(
            await templateHelper.IsLabelPreviewButtonVisibleAsync(templateName),
            "Label preview button should be visible because 'E2E Quality Gate' (global) matches the resolved label");

        // Step 2: Expand the preview and confirm "E2E Quality Gate" is listed.
        await templateHelper.ToggleLabelPreviewAsync(templateName);
        Assert.True(await templateHelper.IsLabelPreviewExpandedAsync(templateName), "Preview should be expanded");

        var initialQgs = await templateHelper.GetPreviewQualityGatesAsync();
        Assert.Contains("E2E Quality Gate", initialQgs);

        // Close the preview
        await templateHelper.ToggleLabelPreviewAsync(templateName);
        Assert.False(await templateHelper.IsLabelPreviewExpandedAsync(templateName), "Preview should be collapsed");

        // Step 3: Add a labelled QG config "Dotnet Gate" (MatchLabels = "dotnet")
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Quality Gate Configs");
        await qgHelper.ClickAddAsync();
        await qgHelper.FillDisplayNameAsync("Dotnet Gate");
        await qgHelper.FillMatchLabelsAsync("dotnet");
        await qgHelper.FillCompilationCommandAsync("dotnet build");
        await qgHelper.FillTestCommandAsync("dotnet test");
        await qgHelper.SaveAsync();
        Assert.True(await qgHelper.IsStatusMessageVisibleAsync("saved"), "Expected 'saved' status after adding Dotnet Gate");

        // Step 4: Navigate back to /agent-coding and verify "Dotnet Gate" appears in preview.
        // TODO [WARNING]: NavigateAsync uses a fixed 3s delay; use WaitForLabelPreviewButtonAsync before
        // ToggleLabelPreviewAsync to ensure the button is present before clicking (avoids race on slow CI)
        await templateHelper.NavigateAsync();
        await templateHelper.ToggleLabelPreviewAsync(templateName);
        Assert.True(await templateHelper.IsLabelPreviewExpandedAsync(templateName));

        var qgsWithDotnetGate = await templateHelper.GetPreviewQualityGatesAsync();
        Assert.Contains("Dotnet Gate", qgsWithDotnetGate);
        Assert.Contains("E2E Quality Gate", qgsWithDotnetGate); // global QG always present

        // Close the preview
        await templateHelper.ToggleLabelPreviewAsync(templateName);

        // Step 5: Disable "Dotnet Gate" via Settings.
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Quality Gate Configs");
        await qgHelper.EditRowAsync("Dotnet Gate");
        await qgHelper.SetEnabledAsync(false);
        await qgHelper.SaveAsync();
        Assert.True(await qgHelper.IsStatusMessageVisibleAsync("saved"), "Expected 'saved' status after disabling Dotnet Gate");

        // Step 6: Navigate back to /agent-coding and verify "Dotnet Gate" is gone from preview.
        // The button remains visible because "E2E Quality Gate" (global, enabled) still matches.
        // TODO [WARNING]: NavigateAsync uses a fixed 3s delay; use WaitForLabelPreviewButtonAsync for determinism
        await templateHelper.NavigateAsync();
        Assert.True(
            await templateHelper.IsLabelPreviewButtonVisibleAsync(templateName),
            "Label preview button should remain visible — E2E Quality Gate (global) still matches");

        await templateHelper.ToggleLabelPreviewAsync(templateName);
        Assert.True(await templateHelper.IsLabelPreviewExpandedAsync(templateName));

        var qgsAfterDisable = await templateHelper.GetPreviewQualityGatesAsync();
        Assert.DoesNotContain("Dotnet Gate", qgsAfterDisable);
        Assert.Contains("E2E Quality Gate", qgsAfterDisable); // global QG still listed
    }
}
