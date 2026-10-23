using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the routing editor (spec 053) on the Routing settings page and on the repository page's Binding
/// card. Rows are named <c>default</c>, <c>implementation</c>, <c>review</c>, <c>decomposition</c>, <c>triage</c> and
/// <c>consolidation</c>.
/// </summary>
public sealed class RouteTableHelper
{
    private readonly IPage _page;

    public RouteTableHelper(IPage page) => _page = page;

    private ILocator Cell(string testId) => _page.Locator($"[data-testid='{testId}']");

    /// <summary>Gives the row a route; a row that inherits is switched to a route first.</summary>
    public async Task SetRouteAsync(string row, string profileId, string model, string? effort = null)
    {
        var setRoute = Cell($"route-set-{row}");
        if (await setRoute.IsVisibleAsync())
        {
            await setRoute.ClickAsync();
            await Cell($"route-profile-{row}").WaitForAsync(new() { Timeout = 5_000 });
        }

        await Cell($"route-profile-{row}").SelectOptionAsync(new SelectOptionValue { Value = profileId });
        var modelInput = Cell($"route-model-{row}");
        await modelInput.FillAsync(model);
        await modelInput.BlurAsync();
        if (effort is not null)
            await Cell($"route-effort-{row}").SelectOptionAsync(new SelectOptionValue { Value = effort });
    }

    /// <summary>What a row without a route says it inherits.</summary>
    public async Task<string> GetInheritTextAsync(string row)
    {
        var cell = Cell($"route-inherit-{row}");
        await cell.WaitForAsync(new() { Timeout = 5_000 });
        return (await cell.TextContentAsync())?.Trim() ?? "";
    }

    /// <summary>The current DOM value of a select in the route table (e.g. <c>route-effort-default</c>).</summary>
    public async Task<string> GetSelectValueAsync(string testId)
    {
        var loc = Cell(testId);
        await loc.WaitForAsync(new() { Timeout = 5_000 });
        return await loc.InputValueAsync();
    }

    /// <summary>
    /// Asserts that the DOM value of a select in the route table equals <paramref name="expected"/>,
    /// retrying with Playwright's built-in polling until <paramref name="timeoutMs"/> expires.
    /// Use this (not <see cref="GetSelectValueAsync"/>) whenever the assertion follows a profile or
    /// tool switch: Blazor Server processes the <c>@bind</c> callback asynchronously over SignalR,
    /// and a one-shot read can execute before the re-render has patched the option list — producing
    /// a false positive on un-fixed code.
    /// </summary>
    public Task AssertSelectValueAsync(string testId, string expected, int timeoutMs = 5_000) =>
        Assertions.Expect(Cell(testId))
            .ToHaveValueAsync(expected, new() { Timeout = timeoutMs });
}

/// <summary>Page object for the Routing settings page (Settings → Agents → Routing).</summary>
public sealed class AgentRoutingSectionHelper
{
    private readonly IPage _page;

    public AgentRoutingSectionHelper(IPage page)
    {
        _page = page;
        Table = new RouteTableHelper(page);
    }

    public RouteTableHelper Table { get; }

    /// <summary>Waits until the routing table has rendered.</summary>
    public Task WaitForTableAsync() =>
        _page.Locator("[data-testid='agent-routing-section'] [data-testid='route-table']").WaitForAsync(new() { Timeout = 15_000 });

    public async Task<string> GetAppliesNoteAsync() =>
        (await _page.Locator("[data-testid='routing-applies-note']").TextContentAsync())?.Trim() ?? "";

    /// <summary>Clicks Save and waits for the success toast.</summary>
    public async Task SaveAsync()
    {
        await _page.ClickAsync("[data-testid='routing-save']");
        await _page.Locator("div.inline-status, div.settings-status").Filter(new() { HasText = "Routing saved" })
            .First.WaitForAsync(new() { Timeout = 10_000 });
    }

    /// <summary>Clicks Save and returns the refusal the page shows, one line per invalid row.</summary>
    public async Task<string> SaveExpectingErrorAsync()
    {
        await _page.ClickAsync("[data-testid='routing-save']");
        var error = _page.Locator("[data-testid='routing-save-error']");
        await error.WaitForAsync(new() { Timeout = 10_000 });
        return (await error.TextContentAsync())?.Trim() ?? "";
    }
}
