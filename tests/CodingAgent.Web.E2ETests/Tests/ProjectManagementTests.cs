using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the project management UI: create, edit, delete, Default protection,
/// moving templates between projects, and name-based template ordering.
/// Issue #3101 (part of #3082).
///
/// <para>
/// Each test seeds its own data through <see cref="E2ETestBase.Fixture"/>.ConfigStore,
/// using constant GUIDs to avoid collisions. <see cref="E2ETestBase.InitializeAsync"/> calls
/// <c>Fixture.ResetAllAsync()</c> before every test, so no cleanup code is needed.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class ProjectManagementTests : E2ETestBase
{
    // ── Stable identifiers ────────────────────────────────────────────────

    // Test 2 — Edit
    private const string EditProjectId = "e2e10001-0000-0000-0000-000000003101";

    // Test 3 — Move via Pipelines page
    private const string MoveTargetProjectId = "e2e10002-0000-0000-0000-000000003101";

    // Test 4 — Template ordering
    private const string OrderProjectId = "e2e10003-0000-0000-0000-000000003101";

    // Test 5 — Name clash
    private const string ClashSourceProjectId = "e2e10004-0000-0000-0000-000000003101";
    private const string ClashTargetProjectId = "e2e10005-0000-0000-0000-000000003101";

    // Test 6 — Delete
    private const string DeleteProjectId = "e2e10006-0000-0000-0000-000000003101";

    // Test 8 — Epic indicator
    private const string EpicProjectId = "e2e10007-0000-0000-0000-000000003101";

    public ProjectManagementTests(E2EFixture fixture) : base(fixture) { }

    // ── Seeding helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Seeds a template with its own unique issue and repository provider configs so that no two
    /// enabled templates share a provider — moves would otherwise fail with
    /// "The repository is already used by the enabled template …".
    /// </summary>
    private async Task SeedTemplateAsync(string projectId, string templateId, string name)
    {
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = $"issue-{templateId}",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = $"Issue {name}"
        }, CancellationToken.None);
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = $"repo-{templateId}",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = $"Repo {name}"
        }, CancellationToken.None);
        await Fixture.ConfigStore.SaveTemplateAsync(projectId, new PipelineJobTemplate
        {
            Id = templateId,
            Name = name,
            IssueProviderId = $"issue-{templateId}",
            RepoProviderId = $"repo-{templateId}",
            Enabled = true
        }, CancellationToken.None);
    }

    // ── Test 1 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProject_AppearsInManageListTreeAndSwitcher()
    {
        var managePage = new ProjectsManagePage(Page, BaseUrl);
        await managePage.NavigateAsync();

        // Create the project via the UI
        var id = await managePage.ClickAddProjectAsync();

        // Navigate to the new project's detail page (NavigateAsync waits for Blazor interactivity)
        var detailPage = new ProjectDetailPage(Page, BaseUrl);
        await detailPage.NavigateAsync(id);
        await detailPage.FillNameAsync("E2E Created Project");
        await detailPage.FillDescriptionAsync("Created by E2E");
        await detailPage.SaveOverviewAsync();

        // Navigate back to the manage list
        await managePage.NavigateAsync();

        // Row visible and has the project name
        await managePage.Row(id).WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        var rowText = await managePage.Row(id).InnerTextAsync();
        Assert.Contains("E2E Created Project", rowText);

        // Tree link visible with the project name
        await managePage.TreeLink(id).WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        var treeLinkText = await managePage.TreeLink(id).InnerTextAsync();
        Assert.Contains("E2E Created Project", treeLinkText);

        // Switcher: CockpitLayout loads projects once per page load — navigate to a full page first
        await Page.GotoCockpitPageAsync($"{BaseUrl}/overview");
        var options = await new ProjectSwitcher(Page).GetOptionTextsAsync();
        Assert.Contains("E2E Created Project", options);
    }

    // ── Test 2 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task EditProjectDescription_PersistsAfterReload()
    {
        // Seed the project
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = EditProjectId,
            Name = "E2E Edit Project",
            Enabled = true,
            TemplateIds = new List<string>()
        }, CancellationToken.None);

        // Use a const so the JS wait condition and the assertion below always refer to the same
        // string. If the value changes, both sides break together rather than the wait silently
        // timing out while the assertion reports a different error.
        const string editedDescription = "Edited by E2E";

        var detailPage = new ProjectDetailPage(Page, BaseUrl);
        await detailPage.NavigateAsync(EditProjectId);
        await detailPage.FillDescriptionAsync(editedDescription);
        await detailPage.SaveOverviewAsync();

        // Reload the same page
        await detailPage.NavigateAsync(EditProjectId);

        // Wait until the async LoadDataAsync has populated the description with the saved value.
        // NavigateAsync's fixed 500ms wait is insufficient on slow CI runners where the
        // Blazor Server round-trip takes longer. Polling the DOM value is deterministic: the
        // assertion fires only when the actual persisted value is visible to the user.
        await Page.WaitForFunctionAsync(
            $"() => document.querySelector('[data-testid=\"project-description-input\"]')?.value === '{editedDescription}'",
            null,
            new() { Timeout = 5_000 });

        // The persisted value must appear in the description input
        var value = await Page.Locator("[data-testid='project-description-input']").InputValueAsync();
        Assert.Equal(editedDescription, value);
    }

    // ── Test 3 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task MoveTemplate_FromPipelinesPage_RegroupsUnderTargetProject()
    {
        // Seed the target project
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = MoveTargetProjectId,
            Name = "E2E Move Target",
            Enabled = true,
            TemplateIds = new List<string>()
        }, CancellationToken.None);

        // Seed the template into the Default project
        await SeedTemplateAsync(WellKnownIds.DefaultProjectId, "e2e-move-tpl", "E2E Move Template");

        var helper = new TemplateTableSectionHelper(Page, BaseUrl);
        await helper.NavigateAsync();

        // Verify the template is initially in Default
        Assert.True(await helper.IsTemplateInGroupAsync("E2E Move Template", "Default"),
            "Template should start in the Default group");

        // Move it to the target project
        await helper.MoveTemplateAsync("E2E Move Template", "E2E Move Target");

        // Poll until the template appears in the target group (menu close ≠ move persisted)
        await WaitUntilAsync(
            async () => await helper.IsTemplateInGroupAsync("E2E Move Template", "E2E Move Target"),
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(200));

        Assert.True(await helper.IsTemplateInGroupAsync("E2E Move Template", "E2E Move Target"),
            "Template should be in E2E Move Target group after move");
        Assert.False(await helper.IsTemplateInGroupAsync("E2E Move Template", "Default"),
            "Template should no longer be in Default group after move");
    }

    // ── Test 4 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TemplatesTab_ListsTemplatesByNameIgnoringCase()
    {
        // Seed the project
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = OrderProjectId,
            Name = "E2E Order Project",
            Enabled = true,
            TemplateIds = new List<string>()
        }, CancellationToken.None);

        // Seed templates in a non-alphabetical order (Bravo, alpha, Charlie)
        // to confirm the UI sorts them by name case-insensitively
        await SeedTemplateAsync(OrderProjectId, "e2e-order-b", "Bravo");
        await SeedTemplateAsync(OrderProjectId, "e2e-order-a", "alpha");
        await SeedTemplateAsync(OrderProjectId, "e2e-order-c", "Charlie");

        var detailPage = new ProjectDetailPage(Page, BaseUrl);
        await detailPage.NavigateAsync(OrderProjectId);
        await detailPage.SelectTabAsync("Templates");

        // Names must be sorted case-insensitively: alpha < Bravo < Charlie
        var names = await detailPage.GetTemplateNamesAsync();
        Assert.Equal(new[] { "alpha", "Bravo", "Charlie" }, names);

        // Hint text is visible (partial match — full text is longer than the issue quote)
        await Page.WaitForSelectorAsync(
            ".form-hint:has-text('Listed by name, which is also the order the loop uses.')",
            new() { Timeout = 5_000 });
    }

    // ── Test 5 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task MoveTemplate_ToProjectWithSameEnabledName_IsRefusedWithReason()
    {
        // Seed source project with template "alpha"
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = ClashSourceProjectId,
            Name = "E2E Clash Source",
            Enabled = true,
            TemplateIds = new List<string>()
        }, CancellationToken.None);
        await SeedTemplateAsync(ClashSourceProjectId, "e2e-clash-src", "alpha");

        // Seed target project with a different template also named "alpha"
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = ClashTargetProjectId,
            Name = "E2E Clash Target",
            Enabled = true,
            TemplateIds = new List<string>()
        }, CancellationToken.None);
        await SeedTemplateAsync(ClashTargetProjectId, "e2e-clash-dst", "alpha");

        // Open source project's Templates tab
        var detailPage = new ProjectDetailPage(Page, BaseUrl);
        await detailPage.NavigateAsync(ClashSourceProjectId);
        await detailPage.SelectTabAsync("Templates");

        // Attempt to move — the API should refuse (duplicate name in target)
        await detailPage.MoveTemplateAsync("alpha", ClashTargetProjectId);

        // Wait for the error inline status to appear
        await Page.WaitForSelectorAsync(".inline-status-error", new() { Timeout = 10_000 });

        // Verify the exact error message in the status span
        var errorText = await Page.Locator(".inline-status-error span").InnerTextAsync();
        Assert.Equal(
            "Failed to move template: The project already has an enabled template named \"alpha\". Template names are unique within a project.",
            errorText);

        // Source project must still have "alpha"
        var names = await detailPage.GetTemplateNamesAsync();
        Assert.Equal(new[] { "alpha" }, names);
    }

    // ── Test 6 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteProject_MovesItsTemplatesToDefault()
    {
        // Seed the project with two templates
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = DeleteProjectId,
            Name = "E2E Delete Project",
            Enabled = true,
            TemplateIds = new List<string>()
        }, CancellationToken.None);
        await SeedTemplateAsync(DeleteProjectId, "e2e-del-1", "E2E Delete One");
        await SeedTemplateAsync(DeleteProjectId, "e2e-del-2", "E2E Delete Two");

        var managePage = new ProjectsManagePage(Page, BaseUrl);
        await managePage.NavigateAsync();

        // Click the Delete button to show the confirm panel
        await managePage.DeleteButton(DeleteProjectId).ClickAsync();

        // Verify the confirm panel shows the correct text
        var confirmPanel = await Page.WaitForSelectorAsync("[data-testid='project-delete-confirm']",
            new() { Timeout = 10_000 });
        var confirmText = await confirmPanel!.TextContentAsync();
        Assert.Contains("Its templates move to the Default project.", confirmText);

        // Click the confirm Delete button
        await Page.ClickAsync("[data-testid='project-delete-confirm-button']");

        // Wait for success status
        await Page.WaitForSelectorAsync(".inline-status-success:has-text('Project deleted.')",
            new() { Timeout = 10_000 });

        // Row and tree link must be gone
        await managePage.Row(DeleteProjectId).WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10_000 });
        Assert.Equal(0, await managePage.Row(DeleteProjectId).CountAsync());
        Assert.Equal(0, await managePage.TreeLink(DeleteProjectId).CountAsync());

        // Templates must now be in the Default project
        var detailPage = new ProjectDetailPage(Page, BaseUrl);
        await detailPage.NavigateAsync(WellKnownIds.DefaultProjectId);
        await detailPage.SelectTabAsync("Templates");

        var defaultNames = await detailPage.GetTemplateNamesAsync();
        Assert.Contains("E2E Delete One", defaultNames);
        Assert.Contains("E2E Delete Two", defaultNames);
    }

    // ── Test 7 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DefaultProject_CannotBeDeletedFromTheUi()
    {
        var managePage = new ProjectsManagePage(Page, BaseUrl);
        await managePage.NavigateAsync();
        // NavigateAsync waits for the Add button but the project list renders asynchronously.
        // Without this wait, the Default project row may not exist yet and deleteBtn.GetAttributeAsync
        // returns null — causing "Assert.Equal() Failure: Values differ".
        await managePage.Row(WellKnownIds.DefaultProjectId).WaitForAsync(
            new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        // Delete button for Default is disabled and has the correct title
        var deleteBtn = managePage.DeleteButton(WellKnownIds.DefaultProjectId);
        Assert.True(await deleteBtn.IsDisabledAsync(),
            "Delete button for Default project must be disabled");
        var title = await deleteBtn.GetAttributeAsync("title");
        Assert.Equal("Cannot delete Default project", title);

        // Note row visible
        await Page.WaitForSelectorAsync(".projects-note:has-text('Cannot be deleted')", new() { Timeout = 5_000 });
        Assert.True(await Page.Locator(".projects-note:has-text('Cannot be deleted')").IsVisibleAsync());

        // No confirm panel rendered (ConfirmDeleteAsync guard prevents it for Default)
        Assert.Equal(0, await Page.Locator("[data-testid='project-delete-confirm']").CountAsync());

        // Default project detail page has no "Project Review" tab
        var detailPage = new ProjectDetailPage(Page, BaseUrl);
        await detailPage.NavigateAsync(WellKnownIds.DefaultProjectId);
        // The initial Blazor render uses _project = new() { Id = "" } before data loads, so
        // IsDefaultProject is false and "Project Review" tab IS shown initially. Wait until
        // the project data loads and the tab disappears (IsDefaultProject becomes true).
        await Page.Locator(".tab-btn:text-is('Project Review')")
            .WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });
        Assert.Equal(0, await Page.Locator(".tab-btn:text-is('Project Review')").CountAsync());
    }

    // ── Test 8 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetEpicIssueProvider_ShowsPuzzleOnPipelinesGroupHeader()
    {
        // Seed the project — the Pipelines page shows a project group only when it has a template
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = EpicProjectId,
            Name = "E2E Epic Project",
            Enabled = true,
            TemplateIds = new List<string>()
        }, CancellationToken.None);
        await SeedTemplateAsync(EpicProjectId, "e2e-epic-tpl", "E2E Epic Template");

        // Seed the issue provider used as the epic issue provider explicitly — this test must
        // not rely on a provider created by another test that may run in a different order.
        // Using "issue-e2e" (the previous value) was fragile: that ID did not belong to any
        // provider seeded in this test and the select option was only available when a specific
        // other test had run before this one in the same shard.
        const string epicProviderSeedId = "issue-epic-e2e-seed-7";
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = epicProviderSeedId,
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "E2E Epic Issue Provider"
        }, CancellationToken.None);

        // Open the project Overview and set the epic issue provider
        var detailPage = new ProjectDetailPage(Page, BaseUrl);
        await detailPage.NavigateAsync(EpicProjectId);
        await detailPage.SelectEpicIssueProviderAsync(epicProviderSeedId);
        await detailPage.SaveOverviewAsync();

        // Pipelines page must show the epic indicator in the project group header
        var helper = new TemplateTableSectionHelper(Page, BaseUrl);
        await helper.NavigateAsync();
        await helper.EpicIndicator("E2E Epic Project").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        Assert.True(await helper.EpicIndicator("E2E Epic Project").IsVisibleAsync(),
            "Epic indicator must be visible in the Pipelines page group header");

        // Manage list must show the puzzle icon in the project row
        var managePage = new ProjectsManagePage(Page, BaseUrl);
        await managePage.NavigateAsync();
        Assert.True(
            await managePage.Row(EpicProjectId).Locator(".projects-epic-icon").IsVisibleAsync(),
            "projects-epic-icon must be visible in the project row after setting epic provider");
    }
}
