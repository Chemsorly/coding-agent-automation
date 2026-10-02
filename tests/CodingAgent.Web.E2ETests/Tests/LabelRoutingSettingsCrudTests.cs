using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for label-routing settings CRUD and the template label preview.
///
/// Covers four scenarios from issue #3100:
/// 1. Agent Profile CRUD (add, edit priority, disable, delete)
/// 2. Quality Gate Config CRUD (add, edit command, disable, delete)
/// 3. Reviewer Config CRUD and Reset-to-Defaults
/// 4. Label preview on Pipelines (QG appears after add, disappears after disable)
///
/// Routing dispatch is already covered headless by DbModeAgentLifecycleTests.
/// These tests focus exclusively on the UI CRUD flows.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class LabelRoutingSettingsCrudTests : E2ETestBase
{
    public LabelRoutingSettingsCrudTests(E2EFixture fixture) : base(fixture) { }

    // ── Scenario 1: Agent Profile CRUD ────────────────────────────────────

    /// <summary>
    /// Verifies the full Agent Profile CRUD lifecycle:
    /// add a profile → edit priority → disable → delete.
    /// Asserts the table and status toasts reflect each step, including after a page reload.
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
        var initialNames = await profiles.GetTableRowNamesAsync();
        Assert.Empty(initialNames);

        // Act: add a profile
        await profiles.ClickAddAsync();
        await profiles.FillDisplayNameAsync("E2E Test Profile");
        await profiles.FillMatchLabelsAsync("e2e,dotnet");
        await profiles.SelectAgentProviderAsync("E2E Agent Provider");
        await profiles.FillPriorityAsync(5);
        await profiles.SetEnabledAsync(true);
        await profiles.SaveAsync();

        // Assert: row appears, save toast shown
        // TODO [WARNING]: Assert.Contains strips only "DEFAULT"; use Any(n => n.StartsWith(...)) to be resilient to other badge suffixes
        var namesAfterAdd = await profiles.GetTableRowNamesAsync();
        Assert.Contains("E2E Test Profile", namesAfterAdd.Select(n => n.Replace("DEFAULT", "").Trim()));
        Assert.True(await profiles.IsStatusMessageVisibleAsync("saved"), "Expected 'saved' status message after add");

        // Assert: persists after reload (issue criterion: "table reflects each step after reload")
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Agent Profiles");
        var namesAfterAddReload = await profiles.GetTableRowNamesAsync();
        Assert.Contains("E2E Test Profile", namesAfterAddReload.Select(n => n.Replace("DEFAULT", "").Trim()));

        // Act: edit priority
        await profiles.EditRowAsync("E2E Test Profile");
        await profiles.FillPriorityAsync(10);
        await profiles.SaveAsync();

        // Assert: save toast still visible (re-renders after save)
        Assert.True(await profiles.IsStatusMessageVisibleAsync("saved"), "Expected 'saved' status message after edit");

        // Assert: profile is still enabled
        // TODO [WARNING]: IsRowEnabledAsync matches any span.status-icon.is-on in the row; scope to the Enabled column to avoid false positives from the Template-match icon
        Assert.True(await profiles.IsRowEnabledAsync("E2E Test Profile"), "Profile should remain enabled after priority edit");

        // TODO [WARNING]: Verify priority edit persisted to store (same pattern as QualityGateConfig test which reads back via LoadQualityGateConfigsAsync)
        // var storedAfterEdit = await Fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None);
        // Assert.Equal(10, storedAfterEdit.Single(p => p.DisplayName == "E2E Test Profile").Priority);

        // TODO [WARNING]: Scenario 1's headless assignment check ("A dispatched job for labels e2e,dotnet uses this profile while enabled") is not
        // performed here. DbModeAgentLifecycleTests seeds its own profiles and does not specifically assert this profile is chosen.
        // Consider adding an assignment assertion using AgentProfileResolver directly.

        // Act: disable the profile
        await profiles.EditRowAsync("E2E Test Profile");
        await profiles.SetEnabledAsync(false);
        await profiles.SaveAsync();

        // Assert: row now shows disabled icon
        Assert.False(await profiles.IsRowEnabledAsync("E2E Test Profile"), "Profile row should show disabled icon after disabling");

        // Assert: disabled state persists after reload
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Agent Profiles");
        Assert.False(await profiles.IsRowEnabledAsync("E2E Test Profile"), "Profile row should still show disabled icon after reload");

        // Act: delete the profile
        await profiles.DeleteRowAsync("E2E Test Profile");

        // Assert: row removed
        // TODO [WARNING]: Assert.Empty(stored) is fragile if another test left a profile in the store; prefer Assert.DoesNotContain
        var namesAfterDelete = await profiles.GetTableRowNamesAsync();
        Assert.DoesNotContain("E2E Test Profile", namesAfterDelete.Select(n => n.Replace("DEFAULT", "").Trim()));
        Assert.True(await profiles.IsStatusMessageVisibleAsync("deleted"), "Expected 'deleted' status message after delete");

        // Assert via store: profile no longer present
        var stored = await Fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None);
        Assert.DoesNotContain(stored, p => p.DisplayName == "E2E Test Profile");
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
    /// - After setting DefaultRequiredAgentLabels and adding a labelled QG config, the preview
    ///   lists the QG config.
    /// - After disabling the QG config, the preview no longer lists it.
    /// - The pre-seeded global "E2E Quality Gate" (empty MatchLabels) keeps the button visible
    ///   throughout; "button absent" is never the expected state in this test.
    ///
    /// Setup uses Option A (simplest): set DefaultRequiredAgentLabels on the pipeline config to
    /// "dotnet" — no new ProviderConfig or template needed; the seeded "E2E Repo Provider" and
    /// the seeded template are reused.
    /// </summary>
    [Fact]
    public async Task TemplateLabelPreview_ShowsAndHidesWithQualityGateEnabled()
    {
        // TODO [WARNING]: This test mutates shared fixture state (DefaultRequiredAgentLabels, template) without
        // restoring it in a teardown step. If test ordering causes this test to run before other tests that read
        // pipeline config or the template table, those tests may see unexpected data. Consider a try/finally
        // block or DisposeAsync override to restore DefaultRequiredAgentLabels to its original value.

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
            Enabled = true
        }, CancellationToken.None);

        // Set DefaultRequiredAgentLabels so LabelResolver returns non-empty labels for the template.
        // This causes GetLabelPreview to return labels and renders the btn-label-preview button.
        var pipelineConfig = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(
            pipelineConfig with { DefaultRequiredAgentLabels = "dotnet" },
            CancellationToken.None);

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
