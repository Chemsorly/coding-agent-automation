using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Playwright;
using System.Text.Json;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Tests the closed-loop circuit breaker: when all templates fail their polls the loop
/// pauses itself, the UI reflects the paused state on both the Pipelines and Overview
/// pages, and an operator can resume dispatching. Also covers the four Overview loop-card
/// states (idle, active, scheduler unavailable, scoped operator).
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class LoopCircuitBreakerTests : E2ETestBase
{
    public LoopCircuitBreakerTests(E2EFixture fixture) : base(fixture) { }

    // ── Private helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Saves config with ClosedLoopPollInterval = 1 s and ClosedLoopCircuitBreakerCooldown = 30 min;
    /// saves an enabled template "template-circuit" (issue-e2e, repo-e2e);
    /// seeds a routed profile with JobTemplate = E2ERouting.Key("e2e").
    /// </summary>
    private async Task SeedLoopAsync()
    {
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            ClosedLoopPollInterval = TimeSpan.FromSeconds(1),
            ClosedLoopCircuitBreakerCooldown = TimeSpan.FromMinutes(30)
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-circuit",
            Name = "template-circuit",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-circuit",
            DisplayName = "Circuit Breaker Profile",
            JobTemplate = E2ERouting.Key("e2e"),
            Enabled = true
        });
    }

    /// <summary>
    /// Polls GET /api/ui/loop/status with Fixture.CreateUiApiClient(), read with PipelineJsonOptions.Default,
    /// every 100 ms for up to 45 s, until predicate holds. Returns the status.
    /// </summary>
    private async Task<UiLoopStatus> WaitForLoopStatusAsync(Func<UiLoopStatus, bool> predicate)
    {
        using var client = Fixture.CreateUiApiClient();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync("/api/ui/loop/status");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var status = JsonSerializer.Deserialize<UiLoopStatus>(json, PipelineJsonOptions.Default);
                if (status is not null && predicate(status))
                    return status;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("Loop status predicate not met within 45 seconds");
    }

    private ILocator Card => Page.Locator("[data-testid=overview-loop-card]");

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenAllTemplatesKeepFailing_ShouldTripTheBreaker_ShowIt_AndResumeDispatches()
    {
        // Arrange
        await SeedLoopAsync();

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "9001",
            Title = "Circuit breaker test issue",
            Description = "## Requirements\nTest issue for circuit breaker E2E\n\n## Acceptance Criteria\n- [ ] Dispatched after circuit breaker resumes",
            Labels = ["agent:next"]
        });

        await using var agent = new FakeAgentClient("circuit-agent-1", "e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        Fixture.IssueProvider.ShouldFail = true;

        try
        {
            // Act: start the loop — polls will fail, tripping the circuit breaker
            Assert.True(await Fixture.StartLoopAsync(), "StartLoopAsync should return true with valid config");

            await WaitUntilAsync(() => Fixture.SchedulerFactory.LoopService.IsCircuitBroken, TimeSpan.FromSeconds(45));
            var status = await WaitForLoopStatusAsync(s => s.IsCircuitBroken);

            // Assert: while tripped, no job is dispatched
            Assert.False(agent.JobAssigned.Task.IsCompleted, "No job should be dispatched while circuit is broken");
            Assert.Equal("⚠️ Loop paused — all 1 templates failing. Auto-resume in 30 min.", status.StatusMessage);

            // Assert: /pipelines page shows the circuit-broken bar and Resume button
            var codingPage = new AgentCodingPage(Page, BaseUrl);
            await codingPage.NavigateAsync();
            await Page.Locator(".agent-summary-bar").WaitForAsync(new() { Timeout = 10_000 });
            Assert.Contains(
                "Loop paused — all 1 templates failing",
                await Page.Locator(".agent-summary-bar").TextContentAsync());
            await Page.Locator(".agent-summary-bar button.btn-save:has-text('Resume')").WaitForAsync(new() { Timeout = 5_000 });

            // Assert: /overview card shows "Loop paused — circuit broken"
            await Page.GotoCockpitPageAsync($"{BaseUrl}/overview");
            await Page.Locator("[data-testid=overview-loop-card]:has-text('Loop paused — circuit broken')").WaitForAsync(new() { Timeout = 10_000 });
            Assert.Contains("Loop paused — circuit broken", await Card.TextContentAsync());

            // Act: clear failure flag BEFORE resuming to prevent immediate re-trip, then click Resume
            Fixture.IssueProvider.ShouldFail = false;
            await codingPage.NavigateAsync();
            await Page.Locator(".agent-summary-bar button.btn-save:has-text('Resume')").ClickAsync();

            // Assert: summary bar disappears (circuit untripped)
            await Page.Locator(".agent-summary-bar:has-text('Loop paused')").WaitForAsync(
                new() { State = WaitForSelectorState.Hidden, Timeout = 20_000 });

            // Assert: API reports circuit no longer broken
            await WaitForLoopStatusAsync(s => !s.IsCircuitBroken);

            // Assert: agent receives the queued issue after resume
            var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("9001", assignment.IssueIdentifier);
        }
        finally
        {
            if (agent.JobAssigned.Task.IsCompleted)
                await agent.AcceptAndCompleteJobAsync((await agent.JobAssigned.Task).JobId);
            await Fixture.StopLoopAsync();
        }
    }

    [Fact]
    public async Task WhenTheLoopIsIdle_ShouldShowIdleOnTheOverview()
    {
        // Arrange: ensure the loop is not running and the status has been reported
        // (ResetAllAsync in InitializeAsync stops any running loop; wait for the status to confirm)
        await WaitForLoopStatusAsync(s => !s.IsLoopActive && s.ReportedAt is not null);

        // Act
        await Page.GotoCockpitPageAsync($"{BaseUrl}/overview");

        // Assert
        await Page.Locator("[data-testid=overview-loop-card]:has-text('Loop idle')").WaitForAsync(new() { Timeout = 10_000 });
        var text = await Card.TextContentAsync();
        Assert.Contains("Loop idle", text);
        Assert.Contains("Start the closed loop from Pipelines to auto-dispatch queued work.", text);
    }

    [Fact]
    public async Task WhenTheLoopRuns_ShouldShowActiveOnTheOverview()
    {
        await SeedLoopAsync();

        try
        {
            // Arrange: start the loop and wait for active status
            Assert.True(await Fixture.StartLoopAsync());
            await WaitForLoopStatusAsync(s => s.IsLoopActive);

            // Act
            await Page.GotoCockpitPageAsync($"{BaseUrl}/overview");

            // Assert
            await Page.Locator("[data-testid=overview-loop-card]:has-text('Closed-loop active')").WaitForAsync(new() { Timeout = 10_000 });
            var text = await Card.TextContentAsync();
            Assert.Contains("Closed-loop active", text);
            Assert.Contains("Polling template", text);
        }
        finally
        {
            await Fixture.StopLoopAsync();
        }
    }

    [Fact]
    public async Task WhenTheStatusCannotBeRead_ShouldShowSchedulerUnavailable()
    {
        // Arrange: intercept the browser's status fetch BEFORE navigating so that
        // Overview's OnInitializedAsync sees a 500 on the first call
        await Page.RouteAsync("**/api/ui/loop/status", r => r.FulfillAsync(new() { Status = 500 }));

        // Act
        await Page.GotoCockpitPageAsync($"{BaseUrl}/overview");

        // Assert
        var text = await Card.TextContentAsync();
        Assert.Contains("Scheduler unavailable", text);
        Assert.Contains("Loop controls are disabled until the scheduler is reachable.", text);
    }

    [Fact]
    public async Task WhenAScopedOperatorOpensTheOverview_ShouldNotShowTheLoopCard()
    {
        // Arrange: open a fresh browser context as alice (project-scoped operator only —
        // no global read access, so the loop card must not be rendered)
        var browser = await Fixture.GetBrowserAsync();
        var context = await browser.NewContextAsync();
        await using (context)
        {
            var page = await context.NewPageAsync();

            // Sign in as alice with TeamGroup claims (operator on the team project only).
            // GotoCockpitPageAsync follows the server-side redirect from /test/sign-in to /user
            // and waits for the cockpit shell to be interactive before returning.
            var signInUrl = E2ETestSignIn.SignInUrl(BaseUrl, "alice", E2ETestSignIn.TeamGroup);
            await page.GotoCockpitPageAsync(signInUrl);

            // Act: open the Overview page
            await page.GotoCockpitPageAsync($"{BaseUrl}/overview");

            // Assert: loop card absent (not global read), stat strip present (page rendered)
            Assert.Equal(0, await page.Locator("[data-testid=overview-loop-card]").CountAsync());
            Assert.True(await page.Locator(".cockpit-stat-strip").IsVisibleAsync(),
                ".cockpit-stat-strip should be visible to confirm the page rendered correctly");
        }
    }
}
