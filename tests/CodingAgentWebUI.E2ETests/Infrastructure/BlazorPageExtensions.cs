using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Playwright extension methods for Blazor Server E2E testing.
/// Based on the official ASP.NET Core testing patterns from
/// https://github.com/dotnet/aspnetcore/blob/main/src/Components/Testing/src/Infrastructure/PlaywrightExtensions.cs
/// </summary>
public static class BlazorPageExtensions
{
    /// <summary>
    /// Waits for the Blazor framework to load on the page by checking that the
    /// global <c>Blazor</c> object exists. This confirms the SignalR circuit is established.
    /// </summary>
    /// <param name="page">The page to wait on.</param>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds.</param>
    public static Task WaitForBlazorAsync(this IPage page, int timeoutMs = 15_000)
        => page.WaitForFunctionAsync(
            "() => typeof Blazor !== 'undefined'",
            null,
            new() { Timeout = timeoutMs });

    /// <summary>
    /// Waits for a Blazor component to become interactive by detecting event handler
    /// registrations on the element matching the CSS selector. Blazor's EventDelegator
    /// stores handler info as an expando property (<c>_blazorEvents_{id}</c>) on DOM
    /// elements when <c>@onclick</c>, <c>@onchange</c>, <c>@bind</c>, etc. are registered.
    /// </summary>
    /// <param name="page">The page to wait on.</param>
    /// <param name="selector">CSS selector identifying the element to check.</param>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds.</param>
    public static Task WaitForInteractiveAsync(this IPage page, string selector, int timeoutMs = 15_000)
        => page.WaitForFunctionAsync("""
            (selector) => {
                const el = document.querySelector(selector);
                return el && Object.getOwnPropertyNames(el)
                    .some(k => k.startsWith('_blazorEvents_'));
            }
            """,
            selector,
            new() { Timeout = timeoutMs });

    /// <summary>
    /// Waits for Blazor enhanced navigation to complete by listening for the 'enhancedload' event.
    /// Call before the action that triggers navigation, then await the returned task.
    /// </summary>
    /// <param name="page">The page to listen on.</param>
    public static async Task WaitForEnhancedNavigationAsync(this IPage page)
    {
        await using var handle = await page.EvaluateHandleAsync(
            "() => new Promise(resolve => Blazor.addEventListener('enhancedload', () => resolve(true), { once: true }))");
    }
}
