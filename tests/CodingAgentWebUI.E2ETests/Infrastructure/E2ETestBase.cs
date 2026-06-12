using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Base class for E2E tests. Provides per-test browser context, page, and fake reset.
/// Takes a screenshot on dispose (CI uploads on failure).
/// </summary>
public abstract class E2ETestBase : IAsyncLifetime
{
    private IBrowserContext? _context;

    protected E2EFixture Fixture { get; }
    protected IPage Page { get; private set; } = null!;
    protected string BaseUrl => Fixture.ServerAddress;

    protected E2ETestBase(E2EFixture fixture)
    {
        Fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        // Reset all state between tests
        Fixture.Factory.ResetAll();

        // Fresh browser context per test (isolated cookies, storage)
        _context = await Fixture.Browser.NewContextAsync();
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
            var runs = Fixture.Factory.HistoryService.GetRunHistory();
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
    /// </summary>
    protected static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(interval);
        }

        throw new TimeoutException(
            $"Condition not met within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s");
    }
}
