using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Base class for E2E tests that drive the UI. Provides a per-test browser context, a page, and
/// fake reset; takes a screenshot on dispose (CI uploads it on failure).
///
/// Tests that assert on database and hub state rather than on rendered pages derive from
/// <see cref="HeadlessE2ETestBase"/> instead — same fixture, no browser.
/// </summary>
public abstract class E2ETestBase : IAsyncLifetime
{
    private IBrowserContext? _context;

    protected E2EFixture Fixture { get; }
    protected IPage Page { get; private set; } = null!;
    protected string BaseUrl => Fixture.ServerAddress;

    /// <summary>Where FakeAgentClient connects: the Pipeline API, which hosts /hubs/agent.</summary>
    protected string AgentHubUrl => Fixture.AgentHubUrl;

    protected E2ETestBase(E2EFixture fixture)
    {
        Fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        // Reset all state between tests
        await Fixture.ResetAllAsync();

        // Fresh browser context per test (isolated cookies, storage)
        var browser = await Fixture.GetBrowserAsync();
        _context = await browser.NewContextAsync();
        await StubExternalFontsAsync(_context);
        Page = await _context.NewPageAsync();

        // Guard: verify DI replacement worked
        var factory = Fixture.Factory.Services.GetRequiredService<CodingAgent.Pipeline.Interfaces.IProviderFactory>();
        if (factory is not Fakes.FakeProviderFactory)
            throw new InvalidOperationException(
                $"DI replacement failed: IProviderFactory resolved as {factory.GetType().Name} instead of FakeProviderFactory");
    }

    /// <summary>
    /// Keeps page loads local to the test server. The Google Fonts stylesheet linked from
    /// App.razor is the only external request on a page load, and it blocks the "load" event that
    /// <c>GotoAsync</c> waits for, so a slow CDN from the CI runner can run navigation into
    /// Playwright's 30s timeout (seen once for /agent-coding). An empty stylesheet leaves the
    /// fallback fonts in place, so no font file is requested either.
    /// </summary>
    private static async Task StubExternalFontsAsync(IBrowserContext context)
    {
        await context.RouteAsync("https://fonts.googleapis.com/**", route => route.FulfillAsync(new RouteFulfillOptions
        {
            Status = 200,
            ContentType = "text/css",
            Body = string.Empty
        }));
        await context.RouteAsync("https://fonts.gstatic.com/**", route => route.AbortAsync());
    }

    public async Task DisposeAsync()
    {
        if (Page is not null)
        {
            // Always take screenshot — CI artifact upload only triggers on failure
            try
            {
                var testName = GetType().Name;
                var screenshotDir = Path.Combine("TestResults", "screenshots");
                Directory.CreateDirectory(screenshotDir);
                var path = Path.Combine(screenshotDir, $"{testName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
                await Page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
            }
            catch
            {
                // Don't fail test teardown if screenshot fails
            }
        }

        if (_context is not null)
            await _context.DisposeAsync();
    }

    // ── Wait Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Polls the history service until a run matching the predicate appears, or times out.
    /// Replaces Task.Delay after completion — deterministic wait instead of arbitrary delay.
    /// </summary>
    protected async Task<PipelineRunSummary> WaitForHistoryAsync(
        Func<PipelineRunSummary, bool> predicate,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);

        while (DateTime.UtcNow < deadline)
        {
            var runs = (await Fixture.Factory.HistoryService.GetRunHistoryAsync());
            var match = runs.FirstOrDefault(predicate);
            if (match is not null) return match;
            await Task.Delay(interval);
        }

        throw new TimeoutException(
            $"No matching run appeared in history within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s");
    }

    /// <summary>
    /// Polls a condition until it returns true, or times out.
    /// Generic replacement for Task.Delay before assertions on server-side state.
    ///
    /// The default ceiling only ever costs a test that is going to fail — the loop returns as soon
    /// as the condition holds — so it is sized against how long a passing test actually needs, not
    /// against a worst case. Measured over a full containerised run: the slowest passing test in
    /// the suite took 8.9s end to end and the 95th percentile was 6.7s, so 25s is roughly three
    /// times the observed need and matches the 30s used by the other helpers here.
    ///
    /// It was 60s, which bought nothing and cost a great deal: eight failing tests sat on it for
    /// 64.8s each — 518s, half of all time spent on failures in that run, against a 15-minute CI
    /// budget for the whole suite.
    /// </summary>
    protected static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(25);
        var deadline = DateTime.UtcNow + effectiveTimeout;
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(interval);
        }

        throw new TimeoutException(
            $"Condition not met within {effectiveTimeout.TotalSeconds}s");
    }

    /// <summary>
    /// Async overload: polls an async condition until it returns true, or times out.
    /// Use this when the condition itself performs async I/O (e.g. awaiting an API call or
    /// <c>CreateDbContextAsync</c>). For synchronous predicates prefer the <c>Func&lt;bool&gt;</c>
    /// overload to avoid the overhead of an async state machine per poll iteration.
    /// </summary>
    protected static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(25);
        var deadline = DateTime.UtcNow + effectiveTimeout;
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(interval);
        }

        throw new TimeoutException(
            $"Condition not met within {effectiveTimeout.TotalSeconds}s");
    }

    // ── Dispatch Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Seeds a template, agent profile, and issue; dispatches via the UI; has the connected
    /// <paramref name="agent"/> accept the job and report <paramref name="step"/>; then waits for
    /// the run service to reflect that step. Returns the active run id.
    ///
    /// <para>
    /// This shared helper avoids duplicating the seed+dispatch+activate boilerplate across test
    /// classes. Each test class that uses it must choose unique issue identifiers (and template
    /// names if needed) to avoid collision within the shared <see cref="E2ECollection"/>.
    /// </para>
    /// </summary>
    protected async Task<string> SeedDispatchAndActivateAsync(
        FakeAgentClient agent,
        string templateName,
        string issueId,
        PipelineStep step = PipelineStep.GeneratingCode)
    {
        // TODO [WARNING]: Template and profile are seeded with hardcoded Ids ("template-1",
        // "profile-e2e"). SaveTemplateAsync/SaveAgentProfileAsync are upserts keyed on Id, so
        // concurrent or sequential callers using different template names but the same Id will
        // silently overwrite each other. The comment below documents that unique issue identifiers
        // are required, but does not mention that the displayed template name will always reflect
        // the last caller's value. If the E2ECollection ever becomes parallel, or if a future
        // test varies the template config (not just the name), the Id must be made unique per
        // caller as well. (DotNetSpecialist review, line 197)
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-1",
            Name = templateName,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-e2e",
            DisplayName = "E2E Agent Profile",
            MatchLabels = new[] { "e2e" },
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = $"Issue {issueId} test",
            Description = "Test",
            Labels = new[] { "enhancement" }
        });

        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();
        await codingPage.SelectTemplateAsync(templateName);
        await codingPage.ClickBrowseIssuesAsync();
        await codingPage.SelectIssueAsync(issueId);
        await codingPage.ClickStartPipelineAsync();

        await Page.WaitForSelectorAsync(".settings-status.status-success", new() { Timeout = 15_000 });
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await agent.AcceptJobAsync(assignment.JobId);
        await agent.ReportStepAsync(assignment.JobId, step);

        var runService = Fixture.RunService;
        await WaitUntilAsync(() => runService.GetActiveRuns().Any(r => r.IssueIdentifier == issueId && r.CurrentStep == step));
        return runService.GetActiveRuns().First(r => r.IssueIdentifier == issueId).RunId;
    }
}
