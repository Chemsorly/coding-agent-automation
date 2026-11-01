using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests covering API failure paths: page load errors (with error card + recovery) and
/// mutation errors (save/cancel failures). Uses Playwright route interception to fault
/// specific /api/ui/ paths with HTTP 500, verifying the UI shows the error, keeps working,
/// and recovers after the fault is cleared.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class ApiFailureUiTests : E2ETestBase
{
    public ApiFailureUiTests(E2EFixture fixture) : base(fixture) { }

    // ── Seed helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds one finished run (issue 9101, "Load error run") and one Pending WorkItem
    /// (issue 9102) used by load-error and work-mutation tests.
    /// Returns the run ID (string) — work item ID must be retrieved from the DB separately.
    /// </summary>
    private async Task<string> SeedDataAsync()
    {
        var runId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier("9101"),
            IssueTitle = "Load error run",
            FinalStep = PipelineStep.Failed,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = now.AddHours(-1),
            CompletedAtOffset = now.AddMinutes(-50),
#pragma warning disable CS0618
            StartedAt = now.AddHours(-1).DateTime,
            CompletedAt = now.AddMinutes(-50).DateTime,
#pragma warning restore CS0618
        });

        await using var db = Fixture.DbContextFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "9102",
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Pending,
            Payload = "{}",
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            TimeoutSeconds = 3600,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
        });
        await db.SaveChangesAsync();

        return runId;
    }

    // ── Load error tests (Theory + reload Facts) ──────────────────────────────

    [Theory]
    [InlineData("/work",      "/api/ui/work-items/active", "Failed to load work:",          "tbody tr:has-text('#9102')")]
    [InlineData("/runs",      "/api/ui/runs",              "Failed to load runs:",           "tbody tr:has-text('#9101')")]
    [InlineData("/fleet",     "/api/ui/agents",            "Failed to load agents:",         ".cockpit-empty:has-text('No agents registered.')")]
    [InlineData("/insights",  "/api/ui/runs",              "Failed to load insights:",       "[data-testid='insights-failed']:has-text('1')")]
    [InlineData("/attention", "/api/ui/runs",              "Failed to load attention items:", "[data-testid='attention-row-9101']")]
    public async Task PageLoadFails_ShowsTheError_AndRefreshRecovers(
        string path, string apiPath, string errorPrefix, string recoveredSelector)
    {
        // Arrange: seed data before faulting, fault before navigating (WASM fetches on first render)
        await SeedDataAsync();
        await using var fault = await Page.FailApiAsync(apiPath, 500, "GET");

        // Act: navigate to the faulted page
        await Page.GotoCockpitPageAsync(BaseUrl + path);

        // Assert: error card is visible and contains the expected prefix and 500
        var errorLocator = Page.Locator($".cockpit-empty:has-text('{errorPrefix}')");
        await Assertions.Expect(errorLocator).ToBeVisibleAsync();
        await Assertions.Expect(errorLocator).ToContainTextAsync("500");

        // Assert: the framework crash bar is NOT visible (page handled the error)
        await Assertions.Expect(Page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();

        // Assert: the page h1 is still visible (layout intact)
        await Assertions.Expect(Page.Locator("h1")).ToBeVisibleAsync();

        // Act: remove the fault and click Refresh
        await fault.DisposeAsync();
        await Page.ClickAsync("button[title='Refresh now']");

        // Assert: error is gone and recovered content is visible
        await Assertions.Expect(errorLocator).ToHaveCountAsync(0);
        await Assertions.Expect(Page.Locator(recoveredSelector)).ToBeVisibleAsync();
    }

    [Fact]
    public async Task KnowledgeLoadFails_ShowsTheError_AndReloadRecovers()
    {
        // Arrange: seed one run so the stat tile shows "1" after recovery
        await SeedDataAsync();
        await using var fault = await Page.FailApiAsync("/api/ui/runs", 500, "GET");

        // Act: navigate to /knowledge while the fault is active
        await Page.GotoCockpitPageAsync(BaseUrl + "/knowledge");

        // Assert: error card with the expected prefix
        var errorLocator = Page.Locator(".cockpit-empty:has-text('Failed to load knowledge metrics:')");
        await Assertions.Expect(errorLocator).ToBeVisibleAsync();
        await Assertions.Expect(errorLocator).ToContainTextAsync("500");
        await Assertions.Expect(Page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
        await Assertions.Expect(Page.Locator("h1")).ToBeVisibleAsync();

        // Act: remove the fault and reload
        await fault.DisposeAsync();
        await Page.ReloadCockpitPageAsync();

        // Assert: stat tile "Runs · recent" shows 1
        var value = await new KnowledgePage(Page, BaseUrl).GetStatTileValueAsync("Runs · recent");
        Assert.Equal("1", value);
    }

    [Fact]
    public async Task RunPageLoadFails_ShowsTheError_AndReloadRecovers()
    {
        // Arrange: seed the run and fault its exact path
        var runId = await SeedDataAsync();
        await using var fault = await Page.FailApiAsync($"/api/ui/runs/{runId}", 500, "GET");

        // Act: navigate to the faulted run page
        await Page.GotoCockpitPageAsync($"{BaseUrl}/runs/{runId}");

        // Assert: run-page container shows the load error, NOT "Run not found"
        var errorLocator = Page.Locator("[data-testid='run-page'] .cockpit-empty");
        await Assertions.Expect(errorLocator).ToBeVisibleAsync();
        await Assertions.Expect(errorLocator).ToContainTextAsync("Failed to load run:");
        await Assertions.Expect(errorLocator).Not.ToContainTextAsync("Run not found");

        // Act: remove the fault and reload
        await fault.DisposeAsync();
        await Page.ReloadCockpitPageAsync();

        // Assert: h1 shows the issue identifier from the seeded run
        await Assertions.Expect(Page.Locator("h1")).ToContainTextAsync("#9101");
    }

    [Fact]
    public async Task SettingsLoadFails_ShowsTheError_AndReloadRecovers()
    {
        // Arrange: fault before navigating
        await using var fault = await Page.FailApiAsync("/api/ui/settings/provider-configs", 500, "GET");

        // Act: navigate to /settings
        await Page.GotoCockpitPageAsync(BaseUrl + "/settings");

        // Assert: inline-status-error shows the load error
        var errorLocator = Page.Locator(".inline-status-error");
        await Assertions.Expect(errorLocator).ToBeVisibleAsync();
        await Assertions.Expect(errorLocator).ToContainTextAsync("Failed to load settings:");

        // Act: remove the fault and reload
        await fault.DisposeAsync();
        await Page.ReloadCockpitPageAsync();

        // Assert: error is gone and the seeded issue provider card is visible
        await Assertions.Expect(errorLocator).ToHaveCountAsync(0);
        await Assertions.Expect(Page.Locator("div.provider-card strong:has-text('E2E Issue Provider')"))
            .ToBeVisibleAsync();
    }

    // ── Work mutation error tests ─────────────────────────────────────────────

    [Fact]
    public async Task WorkRemove_ApiFails_ShowsTheError_ItemStaysQueued()
    {
        // Arrange: seed the pending item, then read its GUID from the DB
        await SeedDataAsync();
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var seeded = await db.WorkItems.AsNoTracking()
            .FirstAsync(w => w.IssueIdentifier == "9102");
        var id = seeded.Id;

        // Navigate before installing the fault so the page loads successfully
        var workPage = new WorkPage(Page, BaseUrl);
        await workPage.NavigateAsync();

        // Install the fault for the cancel path only (POST)
        await using var fault = await Page.FailApiAsync($"/api/ui/work-items/{id}/cancel", 500, "POST");

        // Act: attempt to remove the queued item
        await workPage.RemoveQueuedAsync("9102");

        // Assert: error card shown
        await Assertions.Expect(Page.Locator(".cockpit-empty:has-text('Cancel failed:')")).ToBeVisibleAsync();

        // Assert: the database row is still Pending
        await using var dbCheck = Fixture.DbContextFactory.CreateDbContext();
        var current = await dbCheck.WorkItems.AsNoTracking().FirstAsync(w => w.Id == id);
        Assert.Equal(WorkItemStatus.Pending, current.Status);

        // Act: remove the fault and refresh
        await fault.DisposeAsync();
        await Page.ClickAsync("button[title='Refresh now']");

        // Assert: error is gone and the queue row is back
        await Assertions.Expect(Page.Locator(".cockpit-empty:has-text('Cancel failed:')")).ToHaveCountAsync(0);
        await Assertions.Expect(workPage.QueueRow("9102")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task WorkCancelInFlight_ApiFails_ShowsTheError_ItemStaysDispatched()
    {
        // Arrange: seed a Dispatched work item with a pre-known GUID
        var id = Guid.NewGuid();
        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = id,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "9103",
                IssueProviderConfigId = "issue-e2e",
                Status = WorkItemStatus.Dispatched,
                Payload = "{}",
                AgentSelector = "kiro,dotnet",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                DispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                TimeoutSeconds = 3600,
                ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
            });
            await db.SaveChangesAsync();
        }

        // Navigate before installing the fault
        var workPage = new WorkPage(Page, BaseUrl);
        await workPage.NavigateAsync();

        // Install the fault for the cancel path
        await using var fault = await Page.FailApiAsync($"/api/ui/work-items/{id}/cancel", 500, "POST");

        // Act: attempt to cancel the in-flight run
        await workPage.CancelInFlightAsync("9103");

        // Assert: error card shown
        await Assertions.Expect(Page.Locator(".cockpit-empty:has-text('Cancel failed:')")).ToBeVisibleAsync();

        // Assert: the database row is still Dispatched
        await using var dbCheck = Fixture.DbContextFactory.CreateDbContext();
        var current = await dbCheck.WorkItems.AsNoTracking().FirstAsync(w => w.Id == id);
        Assert.Equal(WorkItemStatus.Dispatched, current.Status);
    }

    [Fact]
    public async Task WorkPriority_ApiFails_ShowsTheError_WeightUnchanged_ThenSaves()
    {
        // Arrange: seed the pending item and read its initial state from the DB
        await SeedDataAsync();
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var seeded = await db.WorkItems.AsNoTracking()
            .FirstAsync(w => w.IssueIdentifier == "9102");
        var id = seeded.Id;
        var initialWeight = seeded.PriorityWeight;

        // Navigate before installing the fault
        var workPage = new WorkPage(Page, BaseUrl);
        await workPage.NavigateAsync();

        // Install the fault for the priority PUT
        await using var fault = await Page.FailApiAsync($"/api/ui/work-items/{id}/priority", 500, "PUT");

        // Act: attempt to set priority (will fail)
        await workPage.SetPriorityAsync("9102", 777);

        // Assert: priority error text is visible (no CSS class — renders via inline style)
        await Assertions.Expect(
            Page.GetByText("Priority update failed:", new() { Exact = false }))
            .ToBeVisibleAsync();

        // Assert: stored weight is unchanged
        await using var dbCheck1 = Fixture.DbContextFactory.CreateDbContext();
        var after = await dbCheck1.WorkItems.AsNoTracking().FirstAsync(w => w.Id == id);
        Assert.Equal(initialWeight, after.PriorityWeight);

        // Assert: queue row is still visible
        await Assertions.Expect(workPage.QueueRow("9102")).ToBeVisibleAsync();

        // Act: remove the fault and try again with a new weight
        await fault.DisposeAsync();
        await workPage.SetPriorityAsync("9102", 778);

        // Assert: error text is gone and the stored weight reaches 778
        await WaitUntilAsync(async () =>
        {
            await using var dbPoll = Fixture.DbContextFactory.CreateDbContext();
            var row = await dbPoll.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
            return row?.PriorityWeight == 778;
        });
        await Assertions.Expect(
            Page.GetByText("Priority update failed:", new() { Exact = false }))
            .ToHaveCountAsync(0);
    }

    // ── Run page cancel error ─────────────────────────────────────────────────

    [Fact]
    public async Task RunPageCancel_ApiFails_ShowsTheCallout_RunStaysLive()
    {
        // Arrange: seed a non-terminal run summary (no WorkItem or agent needed)
        var runId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        await Fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier("9104"),
            IssueTitle = "Cancel error run",
            FinalStep = PipelineStep.GeneratingCode,
            RunType = PipelineRunType.Implementation,
            StartedAtOffset = now.AddMinutes(-2),
#pragma warning disable CS0618
            StartedAt = now.AddMinutes(-2).DateTime,
#pragma warning restore CS0618
        });

        // Fault the cancel endpoint (RunPage uses RunId as the work-item ID for cancel)
        await using var fault = await Page.FailApiAsync($"/api/ui/work-items/{runId}/cancel", 500, "POST");

        // Navigate: do NOT use RunDetailPage.NavigateAsync (waits 12 s for hub subscription)
        await Page.GotoCockpitPageAsync($"{BaseUrl}/runs/{runId}");
        await Page.WaitForSelectorAsync("[data-testid='run-page'][data-initialized='true']",
            new() { Timeout = 12_000 });

        // Act: click Cancel and confirm
        await new RunDetailPage(Page, BaseUrl).CancelAsync(confirm: true);

        // Assert: cancel error callout is shown
        await Assertions.Expect(Page.Locator("[data-testid='cancel-error-callout']"))
            .ToContainTextAsync("Cancel failed:");

        // Assert: no Cancelled badge rendered
        await Assertions.Expect(Page.Locator(".step-badge:has-text('Cancelled')")).ToHaveCountAsync(0);
    }

    // ── Settings save error tests ─────────────────────────────────────────────

    [Fact]
    public async Task SettingsGeneralSave_ApiFails_ShowsTheError_KeepsTheInput_ThenSaves()
    {
        // Arrange: navigate to General settings
        var settings = new SettingsPage(Page, BaseUrl);
        await settings.NavigateAsync();
        await settings.SelectTreeNodeAsync("General");

        // Fault only PUT (UpdatePipelineConfigAsync does GET then PUT on the same URL)
        await using var fault = await Page.FailApiAsync("/api/ui/settings/pipeline", 500, "PUT");

        // Act: fill MaxRetries and click save (button text is "Save General Settings")
        await Page.FillAsync("[data-setting='MaxRetries'] input", "7");
        await Page.ClickAsync("button:has-text('Save General')");

        // Assert: error shown; input still holds the typed value; store unchanged
        await Assertions.Expect(Page.Locator(".inline-status-error"))
            .ToContainTextAsync("Failed to save:");
        await Assertions.Expect(Page.Locator("[data-setting='MaxRetries'] input"))
            .ToHaveValueAsync("7");
        var configAfterFail = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        Assert.Equal(3, configAfterFail.MaxRetries);

        // Act: remove the fault and save again
        await fault.DisposeAsync();
        await Page.ClickAsync("button:has-text('Save General')");

        // Assert: success message appears immediately (auto-clears after 4000 ms)
        await Assertions.Expect(Page.Locator(".inline-status-success"))
            .ToContainTextAsync("General settings saved.");

        // Assert: the store now has MaxRetries = 7
        var configAfterSave = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        Assert.Equal(7, configAfterSave.MaxRetries);
    }

    [Fact]
    public async Task ProjectOverviewSave_ApiFails_ShowsTheError_KeepsTheInput_ThenSaves()
    {
        // Arrange: navigate to the seeded "E2E Team" project
        var projectPage = new ProjectDetailPage(Page, BaseUrl);
        await projectPage.NavigateAsync(E2ETestSignIn.TeamProjectId);

        // Fault only PUT (page also calls GET /api/ui/settings/projects on load for global admin)
        await using var fault = await Page.FailApiAsync("/api/ui/settings/projects", 500, "PUT");

        // Act: fill description and click save directly (SaveOverviewAsync waits for success and would time out)
        await projectPage.FillDescriptionAsync("Unsaved text");
        await Page.ClickAsync("[data-testid='project-save-overview']");

        // Assert: error shown; description input still holds the typed value
        await Assertions.Expect(Page.Locator(".inline-status-error"))
            .ToContainTextAsync("Failed to save:");
        await Assertions.Expect(Page.Locator("[data-testid='project-description-input']"))
            .ToHaveValueAsync("Unsaved text");

        // Assert: the store's project description is still null
        var projectAfterFail = await Fixture.ConfigStore.GetProjectByIdAsync(
            E2ETestSignIn.TeamProjectId, CancellationToken.None);
        Assert.Null(projectAfterFail!.Description);

        // Act: remove the fault and save
        await fault.DisposeAsync();
        await Page.ClickAsync("[data-testid='project-save-overview']");

        // Assert: success message appears immediately (auto-clears after 4000 ms)
        await Assertions.Expect(Page.Locator(".inline-status-success"))
            .ToContainTextAsync("Project overview saved.");

        // Assert: the store now has the description
        var projectAfterSave = await Fixture.ConfigStore.GetProjectByIdAsync(
            E2ETestSignIn.TeamProjectId, CancellationToken.None);
        Assert.Equal("Unsaved text", projectAfterSave!.Description);
    }

    [Fact]
    public async Task ProjectSecretsSave_ApiFails_ShowsTheError_KeepsTheRow()
    {
        // Arrange: navigate to the seeded "E2E Team" project and open the Secrets tab
        var projectPage = new ProjectDetailPage(Page, BaseUrl);
        await projectPage.NavigateAsync(E2ETestSignIn.TeamProjectId);
        await projectPage.SelectTabAsync("Secrets");

        // Add a secret row
        await Page.ClickAsync("button:has-text('+ Add Secret')");
        var keyInput = Page.Locator("input.secret-key-input");
        await keyInput.FillAsync("MY_KEY");
        await keyInput.PressAsync("Tab");
        var valueInput = Page.Locator("input.secret-value-input");
        await valueInput.FillAsync("my-value");
        await valueInput.PressAsync("Tab");

        // Fault only PUT before clicking save
        await using var fault = await Page.FailApiAsync("/api/ui/settings/projects", 500, "PUT");

        // Act: click Save Secrets
        await Page.ClickAsync("button:has-text('Save Secrets')");

        // Assert: error shown; key input still holds the typed value
        await Assertions.Expect(Page.Locator(".inline-status-error"))
            .ToContainTextAsync("Failed to save:");
        await Assertions.Expect(Page.Locator("input.secret-key-input"))
            .ToHaveValueAsync("MY_KEY");

        // Assert: the store's project secrets are still null
        var project = await Fixture.ConfigStore.GetProjectByIdAsync(
            E2ETestSignIn.TeamProjectId, CancellationToken.None);
        Assert.Null(project!.Secrets);
    }
}
