using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Infrastructure;

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
    /// Waits until a cockpit page is interactive and has finished its first data load,
    /// including the project-scope restore that runs in <c>CockpitLayout.OnAfterRenderAsync</c>.
    /// <para>
    /// Cockpit pages are prerendered with their data; when the circuit connects, the interactive
    /// component re-runs <c>OnInitializedAsync</c> and shows a "Loading…" placeholder until its API
    /// calls return, and prerendered buttons have no handlers. After the initial data load,
    /// <c>CockpitLayout.RestoreSavedProjectScopeAsync</c> reads <c>localStorage</c> and may trigger
    /// a second quiet refresh on project-scoped pages. This helper waits until:
    /// </para>
    /// <list type="number">
    ///   <item>The layout's theme toggle has a Blazor event handler (circuit rendered the page).</item>
    ///   <item>No <c>.cockpit-empty</c> placeholder starting with "Loading" is present.</item>
    ///   <item>The project-scope <c>&lt;select&gt;</c> (if present) reflects the value stored in
    ///         <c>localStorage</c>, confirming the scope-restore render has completed.</item>
    /// </list>
    /// </summary>
    /// <param name="page">The page to wait on.</param>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds, per step.</param>
    public static async Task WaitForCockpitPageReadyAsync(this IPage page, int timeoutMs = 15_000)
    {
        await page.WaitForInteractiveAsync(".cockpit-theme-toggle", timeoutMs);
        await page.WaitForFunctionAsync(
            "() => ![...document.querySelectorAll('.cockpit-empty')].some(e => e.textContent.trim().startsWith('Loading'))",
            null,
            new() { Timeout = timeoutMs });
        // Wait until the project-scope <select> reflects the value persisted in localStorage.
        // CockpitLayout.RestoreSavedProjectScopeAsync runs in OnAfterRenderAsync(firstRender)
        // and calls State.SetProject, which fires OnProjectChanged on project-scoped pages and
        // triggers a quiet data refresh. Polling until the select matches localStorage ensures
        // that refresh has been triggered (and the select re-rendered) before assertions run.
        // When no <select> is present (Fleet, About, Settings), the predicate returns true immediately.
        await page.WaitForFunctionAsync(
            """
            () => {
                const stored = (localStorage.getItem('cockpit.selectedProjectId') ?? '');
                const sel = document.querySelector("select[aria-label='Project scope']");
                return !sel || sel.value === stored;
            }
            """,
            null,
            new() { Timeout = timeoutMs });
    }

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
