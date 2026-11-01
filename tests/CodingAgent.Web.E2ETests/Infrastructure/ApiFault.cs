using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Makes the browser's calls to one API path fail until disposed. Fulfilled in the browser, so neither the
/// BFF nor the API sees the call. Pass the path the browser calls, which starts with /api/ui/.
/// </summary>
public sealed class ApiFault : IAsyncDisposable
{
    private readonly IPage _page;
    private readonly Func<string, bool> _matches;
    private readonly Func<IRoute, Task> _handler;
    private int _hitCount;

    public int HitCount => _hitCount;

    private ApiFault(IPage page, Func<string, bool> matches, Func<IRoute, Task> handler)
    {
        _page = page;
        _matches = matches;
        _handler = handler;
    }

    internal void Increment() => Interlocked.Increment(ref _hitCount);

    internal static async Task<ApiFault> CreateAsync(IPage page, string path, int statusCode, string? method)
    {
        if (!path.StartsWith("/api/ui/"))
            throw new ArgumentException("path must start with /api/ui/", nameof(path));

        ApiFault? fault = null;
        Func<string, bool> matches = url => new Uri(url).AbsolutePath == path;
        Func<IRoute, Task> handler = async route =>
        {
            if (method is not null &&
                !string.Equals(method, route.Request.Method, StringComparison.OrdinalIgnoreCase))
            {
                await route.FallbackAsync();
                return;
            }
            fault!.Increment();
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = statusCode,
                ContentType = "application/problem+json",
                Body = "{\"detail\":\"Injected fault\"}"
            });
        };

        fault = new ApiFault(page, matches, handler);
        await page.RouteAsync(matches, handler);
        return fault;
    }

    public async ValueTask DisposeAsync()
    {
        try { await _page.UnrouteAsync(_matches, _handler); }
        catch (PlaywrightException) { }
    }
}

public static class PageFaultExtensions
{
    /// <param name="path">Exact path without query string, for example "/api/ui/agents". Sub-paths do not match.</param>
    /// <param name="statusCode">The status to answer with.</param>
    /// <param name="method">An HTTP method to limit the fault to, for example "POST". Null matches every method.</param>
    public static Task<ApiFault> FailApiAsync(
        this IPage page, string path, int statusCode = 500, string? method = null)
        => ApiFault.CreateAsync(page, path, statusCode, method);
}
