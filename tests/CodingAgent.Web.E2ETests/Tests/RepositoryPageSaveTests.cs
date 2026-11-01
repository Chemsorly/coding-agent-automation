using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests verifying that the Binding card, Behaviour card, and Refactoring modal on the
/// repository page (/pipelines/{id}) persist changes and produce the expected assignment.
///
/// Issue #4139. This class dispatches no issues and allocates no issue identifiers.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RepositoryPageSaveTests : E2ETestBase
{
    private const string SaveTemplateId = "repo-save-template";
    private const string SaveTemplateName = "Repo Save";

    public RepositoryPageSaveTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the "Repo Save" template and provider configs for brain and pipeline providers.
    /// The template is seeded with <c>ReviewEnabled = false</c> so clicking the PR Review
    /// slider in test 11 toggles it ON (from false → true), making the assertion unambiguous.
    /// </summary>
    private async Task SeedAsync()
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = SaveTemplateId,
            Name = SaveTemplateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            ImplementationEnabled = true,
            ReviewEnabled = false,   // explicitly false so test 11 can toggle it to true
            HousekeepingEnabled = false
        }, CancellationToken.None);

        // Brain provider: Kind=Repository, RepositoryRole=Brain — required so PageService.BrainProviders includes it
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "brain-u03",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            RepositoryRole = RepositoryRole.Brain,
            DisplayName = "U03 Brain"
        }, CancellationToken.None);

        // Pipeline provider
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "pipeline-u03",
            Kind = ProviderKind.Pipeline,
            ProviderType = "GitHub",
            DisplayName = "U03 CI"
        }, CancellationToken.None);
    }

    /// <summary>
    /// Loads the current template from the store. Throws if not found.
    /// </summary>
    private async Task<PipelineJobTemplate> LoadTemplateAsync()
    {
        var templates = await Fixture.ConfigStore.LoadTemplatesForProjectAsync(
            WellKnownIds.DefaultProjectId, CancellationToken.None);
        return templates.First(t => t.Id == SaveTemplateId);
    }

    // ── Test 10 ───────────────────────────────────────────────────────────

    /// <summary>
    /// Saving the Binding card persists all fields and the values survive a page reload.
    /// </summary>
    [Fact]
    public async Task BindingSave_PersistsAndSurvivesReload()
    {
        await SeedAsync();

        var repoPage = new RepositoryPageObject(Page, BaseUrl);
        await repoPage.NavigateAsync(SaveTemplateId);
        // Wait for the binding card's selects to be rendered
        await Page.WaitForSelectorAsync("[data-testid='binding-card'] select", new() { Timeout = 10_000 });

        var bindingCard = Page.Locator("[data-testid='binding-card']");

        // Select brain provider — use the form-group containing the "Brain Provider" label
        var brainGroup = bindingCard.Locator(".form-group").Filter(
            new LocatorFilterOptions { Has = Page.Locator("label", new PageLocatorOptions()).Filter(new LocatorFilterOptions { HasText = "Brain Provider (optional)" }) });
        await brainGroup.Locator("select").SelectOptionAsync("brain-u03");

        // Wait for brain-sharing-hint to appear (conditional rendering after BrainProviderId is set)
        await Page.WaitForSelectorAsync("[data-testid='brain-sharing-hint']", new() { Timeout = 5_000 });

        // Check Brain Repository Read-Only
        await Page.GetByLabel("Brain Repository Read-Only").CheckAsync();

        // Select pipeline provider
        var pipelineGroup = bindingCard.Locator(".form-group").Filter(
            new LocatorFilterOptions { Has = Page.Locator("label", new PageLocatorOptions()).Filter(new LocatorFilterOptions { HasText = "Pipeline/CI Provider (optional)" }) });
        await pipelineGroup.Locator("select").SelectOptionAsync("pipeline-u03");

        // Fill stack labels and press Tab (field uses @onchange)
        await Page.Locator("[data-testid='binding-stack-labels']").FillAsync("dotnet, dotnet10");
        await Page.Locator("[data-testid='binding-stack-labels']").PressAsync("Tab");

        // Uncheck Enabled (scope to binding card to avoid ambiguity with the pipelines table)
        await bindingCard.Locator($"input[aria-label='Enable {SaveTemplateName}']").UncheckAsync();

        // Click Save (text exactly "Save")
        await bindingCard.Locator("button.btn-save:text-is('Save')").ClickAsync();

        // Assert binding success message (scoped to card — analysis §4.8)
        var successLocator = bindingCard.Locator(".settings-status.status-success");
        await successLocator.WaitForAsync(new() { Timeout = 10_000 });
        Assert.Contains("Binding saved.", await successLocator.TextContentAsync() ?? "", StringComparison.Ordinal);

        // Assert store
        var stored = await LoadTemplateAsync();
        Assert.Equal("brain-u03", stored.BrainProviderId);
        Assert.True(stored.BrainReadOnly);
        Assert.Equal("pipeline-u03", stored.PipelineProviderId);
        Assert.Equal(new[] { "dotnet", "dotnet10" }, stored.RequiredLabels?.ToArray());
        Assert.False(stored.Enabled);

        // Act: reload and verify persistence
        await repoPage.NavigateAsync(SaveTemplateId);
        await Page.WaitForSelectorAsync("[data-testid='binding-card'] select", new() { Timeout = 10_000 });

        // Assert header contains "Disabled"
        Assert.NotNull(await Page.Locator(".feature-badge:has-text('Disabled')").TextContentAsync());

        // Assert brain and pipeline selects show saved values
        var bindingCard2 = Page.Locator("[data-testid='binding-card']");
        var brainGroup2 = bindingCard2.Locator(".form-group").Filter(
            new LocatorFilterOptions { Has = Page.Locator("label", new PageLocatorOptions()).Filter(new LocatorFilterOptions { HasText = "Brain Provider (optional)" }) });
        Assert.Equal("brain-u03", await brainGroup2.Locator("select").InputValueAsync());

        var pipelineGroup2 = bindingCard2.Locator(".form-group").Filter(
            new LocatorFilterOptions { Has = Page.Locator("label", new PageLocatorOptions()).Filter(new LocatorFilterOptions { HasText = "Pipeline/CI Provider (optional)" }) });
        Assert.Equal("pipeline-u03", await pipelineGroup2.Locator("select").InputValueAsync());

        // Assert stack labels value
        Assert.Equal("dotnet, dotnet10",
            await Page.Locator("[data-testid='binding-stack-labels']").InputValueAsync());
    }

    // ── Test 11 ───────────────────────────────────────────────────────────

    /// <summary>
    /// Clicking the PR Review and Housekeeping sliders saves the new state and causes
    /// the browse-prs-btn to appear.
    /// </summary>
    [Fact]
    public async Task BehaviourSwitches_PersistAndChangeTheHeader()
    {
        await SeedAsync();

        var repoPage = new RepositoryPageObject(Page, BaseUrl);
        await repoPage.NavigateAsync(SaveTemplateId);

        // Wait for the behaviour card's save button (confirms card has loaded)
        var behaviourCard = Page.Locator("[data-testid='behaviour-card']");
        await behaviourCard.Locator("button.btn-save").WaitForAsync(new() { Timeout = 10_000 });

        // Click the PR Review slider (starts false → toggle to true)
        await behaviourCard.Locator(
            $"label.toggle-switch:has(input[aria-label='Enable PR Review for {SaveTemplateName}']) .toggle-slider")
            .ClickAsync();

        // Click the Housekeeping slider (starts false → toggle to true)
        await behaviourCard.Locator(
            $"label.toggle-switch:has(input[aria-label='Enable Housekeeping for {SaveTemplateName}']) .toggle-slider")
            .ClickAsync();

        // Wait for housekeeping-limit to appear (conditional on HousekeepingEnabled=true)
        await Page.WaitForSelectorAsync("[data-testid='housekeeping-limit']", new() { Timeout = 5_000 });

        // Fill housekeeping concurrency limit
        await Page.Locator("[data-testid='housekeeping-limit'] input[type='number']").FillAsync("3");
        await Page.Locator("[data-testid='housekeeping-limit'] input[type='number']").PressAsync("Tab");

        // Check the Branch Cleanup checkbox inside housekeeping-cleanup
        await Page.Locator("[data-testid='housekeeping-cleanup'] input[type='checkbox']").CheckAsync();

        // Save
        await repoPage.SaveBehaviourAsync();

        // Assert success message
        var successMsg = await repoPage.GetBehaviourSuccessMessageAsync();
        Assert.Contains("Behaviour saved", successMsg ?? "", StringComparison.Ordinal);

        // Assert store
        var stored = await LoadTemplateAsync();
        Assert.True(stored.ReviewEnabled);
        Assert.True(stored.HousekeepingEnabled);
        Assert.Equal(3, stored.HousekeepingConcurrencyLimit);
        Assert.True(stored.HousekeepingBranchCleanupEnabled);
        Assert.True(stored.ImplementationEnabled);  // unchanged from default

        // Assert browse-prs-btn is now visible (appears when ReviewEnabled=true)
        await Page.WaitForSelectorAsync("[data-testid='browse-prs-btn']", new() { Timeout = 5_000 });
    }

    // ── Test 12 ───────────────────────────────────────────────────────────

    /// <summary>
    /// Saving the Behaviour card with an empty Name is refused with "Name is required."
    /// and the store is unchanged.
    /// </summary>
    [Fact]
    public async Task BehaviourSave_EmptyName_IsRefused()
    {
        await SeedAsync();

        var repoPage = new RepositoryPageObject(Page, BaseUrl);
        await repoPage.NavigateAsync(SaveTemplateId);

        var behaviourCard = Page.Locator("[data-testid='behaviour-card']");
        await behaviourCard.Locator("button.btn-save").WaitForAsync(new() { Timeout = 10_000 });

        // Clear the Name * input and press Tab to trigger @bind change
        var nameInput = behaviourCard.Locator("input[type='text']").First;
        await nameInput.FillAsync("");
        await nameInput.PressAsync("Tab");

        // Save → should fail
        await repoPage.SaveBehaviourAsync();

        // Assert error message (SaveBehaviourAsync waits for .status-success or .status-error)
        var errorLocator = behaviourCard.Locator(".settings-status.status-error");
        await errorLocator.WaitForAsync(new() { Timeout = 5_000 });
        Assert.Contains("Name is required.", await errorLocator.TextContentAsync() ?? "", StringComparison.Ordinal);

        // Assert store: Name unchanged
        var stored = await LoadTemplateAsync();
        Assert.Equal(SaveTemplateName, stored.Name);
    }

    // ── Test 13 ───────────────────────────────────────────────────────────

    /// <summary>
    /// Cancelling the Refactoring Scan modal creates no work item.
    /// </summary>
    [Fact]
    public async Task RefactoringModal_Cancel_CreatesNoWorkItem()
    {
        await SeedAsync();

        var repoPage = new RepositoryPageObject(Page, BaseUrl);
        await repoPage.NavigateAsync(SaveTemplateId);
        // Wait for binding card visible — ensures page data has loaded before clicking header buttons
        await Page.WaitForSelectorAsync("[data-testid='binding-card']",
            new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        // Open modal
        await Page.ClickAsync("button:has-text('Scan for refactorings')");
        var dialog = Page.Locator("[role='dialog']");
        await dialog.WaitForAsync(new() { Timeout = 5_000 });
        Assert.Contains(SaveTemplateName, await dialog.TextContentAsync() ?? "", StringComparison.Ordinal);

        // Cancel — use text selector to match the button regardless of CSS class
        await dialog.Locator("button:has-text('Cancel')").ClickAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5_000 });

        // Dispatch once to process any pending items
        await Fixture.JobController.DispatchOnceAsync();

        // Assert no work items were created
        var pending = await Fixture.WorkItems.GetPendingAsync(10);
        Assert.Empty(pending);
    }

    // ── Test 14 ───────────────────────────────────────────────────────────

    /// <summary>
    /// Starting a Refactoring Scan dispatches a consolidation job with the chosen focus
    /// and AutoDispatch = true to the connected agent.
    /// </summary>
    [Fact]
    public async Task RefactoringModal_Start_AssignsTheChosenFocus()
    {
        await SeedAsync();

        // Routed profile with empty labels — matches any connected agent
        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-repo-scan-e2e",
            DisplayName = "Repo Scan Profile",
            JobTemplate = E2ERouting.Key([]),
            Enabled = true
        });

        await using var agent = new FakeAgentClient("repo-page-scan-agent");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var repoPage = new RepositoryPageObject(Page, BaseUrl);
        await repoPage.NavigateAsync(SaveTemplateId);
        // Wait for the binding card to be visible — this confirms PageService.LoadAsync() has
        // completed and the header buttons (including Scan for refactorings) are interactive.
        // Without this, WaitForSelectorAsync with default Attached state finds the button in
        // prerendered DOM before data loads, and ClickAsync then times out waiting for it to
        // become actionable.
        await Page.WaitForSelectorAsync("[data-testid='binding-card']",
            new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });

        // Open modal
        var scanBtn = Page.Locator("button:has-text('Scan for refactorings')");
        await scanBtn.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        await scanBtn.ClickAsync();
        var dialog = Page.Locator("[role='dialog']");
        await dialog.WaitForAsync(new() { Timeout = 5_000 });
        Assert.Contains(SaveTemplateName, await dialog.TextContentAsync() ?? "", StringComparison.Ordinal);

        // Select "Tests" focus (RefactoringScanFocus.Tests = 4)
        await Page.Locator("[data-testid='refactoring-focus']").SelectOptionAsync(
            new SelectOptionValue { Value = "4" });

        // Check the agent:next auto-dispatch checkbox
        await dialog.Locator("input[type='checkbox']").CheckAsync();

        // Start Scan
        await dialog.Locator("button.btn-save").ClickAsync();

        // Wait for agent to receive the job
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(RefactoringScanFocus.Tests, assignment.RefactoringFocus);
        Assert.True(assignment.AutoDispatch);

        // Assert no error toast
        Assert.Equal(0, await Page.Locator(".settings-status-toast.status-error").CountAsync());

        // Clean up: report consolidation complete so nothing is left running
        await agent.ReportConsolidationCompleteAsync(new ConsolidationJobResult
        {
            JobId = assignment.JobId,
            Success = true,
            CreatedIssues = []
        });

        // Wait for the work item to reach a terminal state
        var workItemId = Guid.Parse(assignment.JobId);
        await WaitUntilAsync(async () =>
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var item = await db.WorkItems.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == workItemId);
            return item is { Status: WorkItemStatus.Succeeded or WorkItemStatus.Failed };
        }, timeout: TimeSpan.FromSeconds(15));
    }
}
