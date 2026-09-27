using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the ✕ (Remove) button in the project detail Templates tab.
/// Verifies issue #3119 / scenario #3101: clicking ✕ on a template in a non-Default project
/// moves it to the Default project rather than leaving it in no project.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class ProjectTemplateRemoveTests : E2ETestBase
{
    public ProjectTemplateRemoveTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RemoveTemplate_FromNonDefaultProject_MovesTemplateToDefault()
    {
        // Arrange: create a non-Default project and seed a template into it
        var nonDefaultProjectId = Guid.NewGuid().ToString();
        var nonDefaultProject = new PipelineProject
        {
            Id = nonDefaultProjectId,
            Name = "Test Project",
            Enabled = true,
            TemplateIds = new List<string>()
        };
        await Fixture.ConfigStore.SaveProjectAsync(nonDefaultProject, CancellationToken.None);

        var templateId = "e2e-remove-template";
        // TODO [WARNING]: Hardcoded template ID risks test pollution in shared/re-run environments.
        // If two test instances run concurrently or the DB isn't cleaned between runs, SaveTemplateAsync
        // may update rather than insert, or stale data from a previous run pollutes the Default
        // project assertion. Use Guid.NewGuid().ToString() for isolation.
        await Fixture.ConfigStore.SaveTemplateAsync(nonDefaultProjectId, new PipelineJobTemplate
        {
            Id = templateId,
            Name = "E2E Remove Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Act: navigate to Settings → Projects → Test Project → Templates tab
        var settingsPage = new SettingsPage(Page, BaseUrl);
        await settingsPage.NavigateAsync();

        // Click on the "Projects" tree node to expand and navigate
        await settingsPage.SelectTreeNodeAsync("Test Project");

        // Click the Templates tab
        await Page.ClickAsync(".tab-btn:has-text('Templates')");
        // Wait for the template-add-row which is unique to the Templates tab content
        await Page.WaitForSelectorAsync(".template-add-row", new() { Timeout = 5_000 });

        // Verify template is visible before removal (must be in the template-list, not just the dropdown)
        var templateList = await Page.WaitForSelectorAsync(".template-list", new() { Timeout = 5_000 });
        var listContent = await templateList!.TextContentAsync();
        Assert.Contains("E2E Remove Template", listContent);

        // Click the ✕ button to remove the template from this project
        await Page.ClickAsync(".btn-icon-danger[title='Remove from project (moves to Default)']");
        // Wait for the template-list to disappear — when the only template is removed the list
        // is no longer rendered (TemplateIds.Count == 0). This is the authoritative signal that
        // MoveTemplateAsync completed and Blazor has re-rendered the template list.
        await Page.WaitForSelectorAsync(".template-list",
            new() { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        // Assert: template is no longer in Test Project's template list
        var templateListAfter = Page.Locator(".template-list");
        Assert.Equal(0, await templateListAfter.CountAsync());

        // Assert: navigate to Default project Templates tab and verify template is there
        await settingsPage.SelectTreeNodeAsync("Default");
        await Page.ClickAsync(".tab-btn:has-text('Templates')");
        // Wait for the template-add-row which is unique to the Templates tab content
        await Page.WaitForSelectorAsync(".template-add-row", new() { Timeout = 5_000 });
        // Wait for the template-list to appear (template was moved here)
        var defaultTemplateList = await Page.WaitForSelectorAsync(".template-list", new() { Timeout = 5_000 });
        var defaultListContent = await defaultTemplateList!.TextContentAsync();
        Assert.Contains("E2E Remove Template", defaultListContent);
    }

    [Fact]
    public async Task RemoveTemplate_DefaultProject_XButtonIsNotRendered()
    {
        // The ✕ button must not be visible on the Default project to prevent accidental orphaning.
        // TODO [WARNING]: Hardcoded template ID "e2e-default-template" risks test pollution in shared
        // or re-run environments. Use Guid.NewGuid().ToString() for isolation.
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "e2e-default-template",
            Name = "Default Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        var settingsPage = new SettingsPage(Page, BaseUrl);
        await settingsPage.NavigateAsync();

        // Navigate to Default project
        await settingsPage.SelectTreeNodeAsync("Default");

        // Click the Templates tab
        await Page.ClickAsync(".tab-btn:has-text('Templates')");
        // Wait for the template-add-row which is unique to the Templates tab content
        await Page.WaitForSelectorAsync(".template-add-row", new() { Timeout = 5_000 });

        // Verify template is visible in the template-list (must be listed, not just in the dropdown)
        var templateList = await Page.WaitForSelectorAsync(".template-list", new() { Timeout = 5_000 });
        var listContent = await templateList!.TextContentAsync();
        Assert.Contains("Default Template", listContent);

        // Assert: no ✕ remove button is rendered for Default project templates
        var removeButtons = await Page.Locator(".btn-icon-danger").CountAsync();
        Assert.Equal(0, removeButtons);
    }
}
