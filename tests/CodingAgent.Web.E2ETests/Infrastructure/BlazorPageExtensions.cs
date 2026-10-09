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

    private const string NoLoadingPlaceholderScript =
        "() => !document.querySelector('.auth-authorizing') && " +
        "![...document.querySelectorAll('.cockpit-empty')].some(e => e.textContent.trim().startsWith('Loading'))";

    /// <summary>
    /// Waits until a cockpit page is interactive and has finished its first data load. Cockpit pages are
    /// prerendered; prerendered elements have no Blazor event handlers, and the circuit's first renders show
    /// CockpitLayout's ".auth-authorizing" placeholder and then the page's ".cockpit-empty" "Loading…" card.
    /// Ready means: the layout's theme toggle has a Blazor event handler (the circuit rendered CockpitLayout),
    /// no ".auth-authorizing" element is left, and no ".cockpit-empty" whose text starts with "Loading" is left.
    /// </summary>
    /// <param name="page">The page to wait on.</param>
    /// <param name="timeoutMs">Maximum time per step (handler, then placeholders), in milliseconds.</param>
    /// <exception cref="TimeoutException">A step did not finish in time; the message names the URL and the step.</exception>
    public static async Task WaitForCockpitPageReadyAsync(this IPage page, int timeoutMs = 15_000)
    {
        try
        {
            await page.WaitForInteractiveAsync(".cockpit-theme-toggle", timeoutMs);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"Cockpit page {page.Url} was not interactive after {timeoutMs} ms: '.cockpit-theme-toggle' has no " +
                "Blazor event handler, so the Blazor Server circuit has not rendered CockpitLayout.", ex);
        }

        try
        {
            await page.WaitForFunctionAsync(NoLoadingPlaceholderScript, null, new() { Timeout = timeoutMs });
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"Cockpit page {page.Url} is interactive but still shows a loading placeholder after {timeoutMs} ms " +
                "('.auth-authorizing', or a '.cockpit-empty' whose text starts with 'Loading').", ex);
        }
    }

    /// <summary>
    /// Loads a cockpit page and waits until it is ready (<see cref="WaitForCockpitPageReadyAsync"/>).
    /// Page objects and tests use this for every cockpit page load instead of <c>GotoAsync</c>.
    /// </summary>
    /// <returns>The main-resource response of the navigation (the last one after redirects).</returns>
    public static async Task<IResponse?> GotoCockpitPageAsync(this IPage page, string url, int timeoutMs = 15_000)
    {
        // TODO [WARNING]: A network-level or navigation-level TimeoutException from GotoAsync is not
        // caught here — it will surface as a raw Playwright message without the caller's URL context.
        // If navigation-timeout diagnostics become a pain point, wrap GotoAsync in a try/catch and
        // rethrow with a message that names the URL and intent (same pattern as WaitForCockpitPageReadyAsync).
        // (DotNetSpecialist finding, BlazorPageExtensions.cs:74)
        var response = await page.GotoAsync(url);
        await page.WaitForCockpitPageReadyAsync(timeoutMs);
        return response;
    }

    /// <summary>Reloads the current cockpit page and waits until it is ready (<see cref="WaitForCockpitPageReadyAsync"/>).</summary>
    public static async Task ReloadCockpitPageAsync(this IPage page, int timeoutMs = 15_000)
    {
        await page.ReloadAsync();
        await page.WaitForCockpitPageReadyAsync(timeoutMs);
    }

    /// <summary>
    /// Waits until the CockpitLayout's project switcher DOM value matches the value stored in
    /// <c>localStorage['cockpit.selectedProjectId']</c>. This confirms that the layout's
    /// <c>OnAfterRenderAsync</c> has completed its localStorage restore and any project-scoped
    /// page has received the <c>OnProjectChanged</c> event and re-queried its data.
    /// <para>
    /// Call this after <see cref="WaitForCockpitPageReadyAsync"/> on pages whose content is
    /// filtered by the selected project (e.g. /work), to avoid reading stale all-projects data
    /// that was loaded before the localStorage restore fired.
    /// </para>
    /// </summary>
    /// <param name="page">The page to wait on.</param>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds.</param>
    public static Task WaitForProjectSwitcherRestoredAsync(this IPage page, int timeoutMs = 15_000)
        => page.WaitForFunctionAsync(
            """
            () => {
                const stored = localStorage.getItem('cockpit.selectedProjectId') ?? '';
                const sel = document.querySelector('select[aria-label="Project scope"]');
                return sel != null && sel.value === stored;
            }
            """,
            null,
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
