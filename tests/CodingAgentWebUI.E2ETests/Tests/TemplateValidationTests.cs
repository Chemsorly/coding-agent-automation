using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Tests that validate template CRUD form validation and error feedback.
/// Ensures the UI shows clear validation messages when form inputs are invalid.
/// </summary>
[Trait("Category", "E2E")]
public sealed class TemplateValidationTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public TemplateValidationTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task AddTemplate_MissingName_ShowsValidationError()
    {
        // Arrange: navigate to agent-coding page
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Act: click "+ Add Template" button
        await Page.ClickAsync("button.btn-add:has-text('Add Template')");
        await Page.WaitForSelectorAsync("div.provider-form", new() { Timeout = 5_000 });

        // Leave name empty, select providers
        // Note: Nth(0) is Project dropdown, Nth(1) is Issue Provider, Nth(2) is Repo Provider
        var issueSelect = Page.Locator("div.provider-form select").Nth(1);
        await issueSelect.SelectOptionAsync(new SelectOptionValue { Label = "E2E Issue Provider" });
        var repoSelect = Page.Locator("div.provider-form select").Nth(2);
        await repoSelect.SelectOptionAsync(new SelectOptionValue { Label = "E2E Repo Provider" });

        // Click Save without filling name
        await Page.ClickAsync("div.form-buttons button.btn-save");
        await Page.WaitForTimeoutAsync(500);

        // Assert: validation error is shown
        var errorMsg = await Page.TextContentAsync("div.provider-form .settings-status.status-error");
        Assert.NotNull(errorMsg);
        Assert.Contains("Name is required", errorMsg);
    }

    [Fact]
    public async Task AddTemplate_MissingIssueProvider_ShowsValidationError()
    {
        // Arrange: navigate to agent-coding page
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Act: click "+ Add Template" button
        await Page.ClickAsync("button.btn-add:has-text('Add Template')");
        await Page.WaitForSelectorAsync("div.provider-form", new() { Timeout = 5_000 });

        // Fill name but leave issue provider empty
        var nameInput = Page.Locator("div.provider-form input[type='text']").First;
        await nameInput.FillAsync("Test Template");

        // Select repo provider but not issue provider
        // Note: Nth(0) is Project dropdown, Nth(1) is Issue Provider, Nth(2) is Repo Provider
        var repoSelect = Page.Locator("div.provider-form select").Nth(2);
        await repoSelect.SelectOptionAsync(new SelectOptionValue { Label = "E2E Repo Provider" });

        // Click Save
        await Page.ClickAsync("div.form-buttons button.btn-save");
        await Page.WaitForTimeoutAsync(500);

        // Assert: validation error is shown
        var errorMsg = await Page.TextContentAsync("div.provider-form .settings-status.status-error");
        Assert.NotNull(errorMsg);
        Assert.Contains("Issue Provider is required", errorMsg);
    }

    [Fact]
    public async Task AddTemplate_DuplicateProviderCombo_ShowsValidationError()
    {
        // Arrange: seed an existing template with issue-e2e + repo-e2e
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "existing-template",
                    Name = "Existing Template",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
        }, CancellationToken.None);

        // Act: navigate and try to add a duplicate
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        await Page.ClickAsync("button.btn-add:has-text('Add Template')");
        await Page.WaitForSelectorAsync("div.provider-form", new() { Timeout = 5_000 });

        // Fill all fields with same provider combination
        var nameInput = Page.Locator("div.provider-form input[type='text']").First;
        await nameInput.FillAsync("Duplicate Template");

        // Note: Nth(0) is Project dropdown, Nth(1) is Issue Provider, Nth(2) is Repo Provider
        var issueSelect = Page.Locator("div.provider-form select").Nth(1);
        await issueSelect.SelectOptionAsync(new SelectOptionValue { Label = "E2E Issue Provider" });
        var repoSelect = Page.Locator("div.provider-form select").Nth(2);
        await repoSelect.SelectOptionAsync(new SelectOptionValue { Label = "E2E Repo Provider" });

        // Click Save
        await Page.ClickAsync("div.form-buttons button.btn-save");
        await Page.WaitForTimeoutAsync(500);

        // Assert: duplicate validation error is shown
        var errorMsg = await Page.TextContentAsync("div.provider-form .settings-status.status-error");
        Assert.NotNull(errorMsg);
        Assert.Contains("already exists", errorMsg);
    }

    [Fact]
    public async Task AddTemplate_Success_AppearsInTableAndShowsMessage()
    {
        // Arrange: ensure no templates exist initially
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = Array.Empty<PipelineJobTemplate>()
        }, CancellationToken.None);

        // Act: navigate and add a template
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        await Page.ClickAsync("button.btn-add:has-text('Add Template')");
        await Page.WaitForSelectorAsync("div.provider-form", new() { Timeout = 5_000 });

        var nameInput = Page.Locator("div.provider-form input[type='text']").First;
        await nameInput.FillAsync("New Valid Template");

        // Note: Nth(0) is Project dropdown, Nth(1) is Issue Provider, Nth(2) is Repo Provider
        var issueSelect = Page.Locator("div.provider-form select").Nth(1);
        await issueSelect.SelectOptionAsync(new SelectOptionValue { Label = "E2E Issue Provider" });
        var repoSelect = Page.Locator("div.provider-form select").Nth(2);
        await repoSelect.SelectOptionAsync(new SelectOptionValue { Label = "E2E Repo Provider" });

        await Page.ClickAsync("div.form-buttons button.btn-save");
        await Page.WaitForTimeoutAsync(1000);

        // Assert: success message appears
        var successMsg = await Page.QuerySelectorAsync(".settings-status.status-success");
        Assert.NotNull(successMsg);
        var successText = await successMsg.TextContentAsync();
        Assert.Contains("New Valid Template", successText);

        // Assert: template appears in the table
        var tableText = await Page.TextContentAsync(".monitoring-table");
        Assert.Contains("New Valid Template", tableText);

        // Assert: form is closed
        var formVisible = await Page.Locator("div.provider-form").CountAsync();
        Assert.Equal(0, formVisible);
    }

    [Fact]
    public async Task RemoveTemplate_ConfirmDialog_RemovesFromTable()
    {
        // Arrange: seed a template
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            PipelineJobTemplates = new[]
            {
                new PipelineJobTemplate
                {
                    Id = "template-to-remove",
                    Name = "Template To Remove",
                    IssueProviderId = "issue-e2e",
                    RepoProviderId = "repo-e2e",
                    Enabled = true
                }
            }
        }, CancellationToken.None);

        // Act: navigate
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Verify template is in the table
        var tableText = await Page.TextContentAsync(".monitoring-table");
        Assert.Contains("Template To Remove", tableText);

        // Click Remove button
        await Page.ClickAsync("button.btn-delete:has-text('Remove')");
        await Page.WaitForTimeoutAsync(500);

        // Assert: confirmation dialog appears
        var confirmDialog = Page.Locator(".agent-detail-confirm");
        var confirmCount = await confirmDialog.CountAsync();
        Assert.True(confirmCount > 0, "Confirmation dialog should appear after clicking Remove");

        var confirmText = await confirmDialog.TextContentAsync();
        Assert.Contains("Template To Remove", confirmText);

        // Act: confirm removal
        await Page.ClickAsync(".confirm-buttons button.btn-delete:has-text('Remove')");
        await Page.WaitForTimeoutAsync(1000);

        // Assert: template is removed — either the table no longer contains it, or the table is gone entirely (0 templates)
        var tableExists = await Page.Locator(".monitoring-table").CountAsync();
        if (tableExists > 0)
        {
            var tableTextAfter = await Page.TextContentAsync(".monitoring-table");
            Assert.DoesNotContain("Template To Remove", tableTextAfter);
        }
        // If table doesn't exist, the template was the only one and removal succeeded

        // Assert: success message appears
        var successMsg = await Page.QuerySelectorAsync(".settings-status.status-success");
        Assert.NotNull(successMsg);
    }

    [Fact]
    public async Task AddTemplate_CancelButton_ClosesForm()
    {
        // Act: navigate and open the add form
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        await Page.ClickAsync("button.btn-add:has-text('Add Template')");
        await Page.WaitForSelectorAsync("div.provider-form", new() { Timeout = 5_000 });

        // Assert: form is visible
        var formVisible = await Page.Locator("div.provider-form").CountAsync();
        Assert.True(formVisible > 0);

        // Act: click Cancel
        await Page.ClickAsync("div.form-buttons button.btn-cancel");
        await Page.WaitForTimeoutAsync(500);

        // Assert: form is closed
        var formVisibleAfter = await Page.Locator("div.provider-form").CountAsync();
        Assert.Equal(0, formVisibleAfter);
    }
}
