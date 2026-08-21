using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

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
        Page = await _context.NewPageAsync();

        // Guard: verify DI replacement worked
        var factory = Fixture.Factory.Services.GetRequiredService<CodingAgentWebUI.Pipeline.Interfaces.IProviderFactory>();
        if (factory is not Fakes.FakeProviderFactory)
            throw new InvalidOperationException(
                $"DI replacement failed: IProviderFactory resolved as {factory.GetType().Name} instead of FakeProviderFactory");
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
}
