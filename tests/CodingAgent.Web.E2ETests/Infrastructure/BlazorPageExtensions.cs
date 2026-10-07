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
    /// a second quiet refresh on project-scoped pages (e.g. Work). This helper waits until all of
    /// the following are true:
    /// </para>
    /// <list type="number">
    ///   <item>The layout's theme toggle has a Blazor event handler (circuit rendered the page).</item>
    ///   <item>The <c>&lt;p class="auth-authorizing"&gt;</c> placeholder is absent, confirming that
    ///         <c>CockpitLayout._accessReady</c> is <c>true</c> and the page body has been rendered.
    ///         The layout briefly shows this paragraph while its <c>OnInitializedAsync</c> runs on
    ///         the reconnecting interactive circuit before <c>_accessReady</c> is restored.</item>
    ///   <item>No <c>.cockpit-empty</c> placeholder starting with "Loading" or "Checking" is present.
    ///         "Checking the provider backlog…" on the Work page disappears only after
    ///         <c>HandleProjectChanged</c> completes its final <c>LoadBacklogAsync</c>, so this check
    ///         also covers the project-filter quiet refresh triggered by the scope restore.</item>
    /// </list>
    /// </summary>
    /// <param name="page">The page to wait on.</param>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds, per step.</param>
    public static async Task WaitForCockpitPageReadyAsync(this IPage page, int timeoutMs = 15_000)
    {
        await page.WaitForInteractiveAsync(".cockpit-theme-toggle", timeoutMs);
        // Wait until:
        // (a) CockpitLayout._accessReady is true — no auth-authorizing paragraph. The layout sets
        //     _accessReady = false at the start of OnInitializedAsync on the reconnecting circuit
        //     and briefly replaces the page body with a loading paragraph, hiding rendered content.
        // (b) All loading/checking placeholders are gone — covers both the initial "Loading X…"
        //     data load AND the Work page's "Checking the provider backlog…" which disappears only
        //     after HandleProjectChanged (triggered by RestoreSavedProjectScopeAsync) fully completes.
        await page.WaitForFunctionAsync(
            """
            () => {
                if (document.querySelector('p.auth-authorizing')) return false;
                const empties = [...document.querySelectorAll('.cockpit-empty')];
                return !empties.some(e => {
                    const t = e.textContent.trim();
                    return t.startsWith('Loading') || t.startsWith('Checking');
                });
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
