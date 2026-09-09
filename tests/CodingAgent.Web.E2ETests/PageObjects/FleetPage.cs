using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the /fleet page — the cockpit replacement for the old monitoring page's
/// "Registered Agents" table. Fleet renders agents in a <c>.monitoring-table</c> where the agent id
/// is a <c>div.monitoring-mono</c> in the first cell (not a <c>td.monitoring-mono</c> as the old page
/// used) and the status is a <c>span.monitoring-status</c> whose text is the bare status (no emoji).
/// </summary>
public sealed class FleetPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public FleetPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>Navigates to /fleet and waits for the agents table (or empty state) to render.</summary>
    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/fleet");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        // Allow the Blazor Server circuit to connect and the first agent load to complete.
        await _page.WaitForTimeoutAsync(2000);
    }

    /// <summary>Status text for an agent (e.g. "Idle", "Busy", "Disconnected"), or null if absent.</summary>
    public async Task<string?> GetAgentStatusAsync(string agentId)
    {
        return await _page.EvaluateAsync<string?>(@"(agentId) => {
            const rows = document.querySelectorAll('.monitoring-table tbody tr');
            for (const row of rows) {
                const mono = row.querySelector('.monitoring-mono');
                if (mono && mono.textContent.trim() === agentId) {
                    const statusSpan = row.querySelector('.monitoring-status');
                    return statusSpan ? statusSpan.textContent.trim() : null;
                }
            }
            return null;
        }", agentId);
    }

    /// <summary>Whether an agent row is present on the page.</summary>
    public async Task<bool> IsAgentVisibleAsync(string agentId)
    {
        return await _page.EvaluateAsync<bool>(@"(agentId) => {
            const rows = document.querySelectorAll('.monitoring-table tbody tr');
            for (const row of rows) {
                const mono = row.querySelector('.monitoring-mono');
                if (mono && mono.textContent.trim() === agentId) return true;
            }
            return false;
        }", agentId);
    }

    // TODO: Add tests to assert (a) no element with label text 'Disconnected' exists in .cockpit-stat-l,
    // and (b) an element with label text containing 'Kiro Credentials' is present. Without these,
    // accidental reversion of the tile removal or rename would go undetected. Consider also adding a
    // generic GetStatTileValueAsync(string labelText) helper to avoid duplicating DOM-traversal logic.
    // See review findings for issue #2329 (TestQualityReviewer warning).
    /// <summary>Total registered agents, read from the "Agents" stat tile.</summary>
    public async Task<int> GetAgentCountAsync()
    {
        return await _page.EvaluateAsync<int>(@"() => {
            const stats = document.querySelectorAll('.cockpit-stat');
            for (const s of stats) {
                const label = s.querySelector('.cockpit-stat-l');
                if (label && label.textContent.trim() === 'Agents') {
                    const val = s.querySelector('.cockpit-stat-v');
                    return val ? parseInt(val.textContent.trim(), 10) || 0 : 0;
                }
            }
            return 0;
        }");
    }

    /// <summary>
    /// Returns true if a stat tile with the given label text is present on the Fleet page.
    /// Use to verify removed tiles (e.g. "Busy", "Idle", "Utilization") are absent.
    /// </summary>
    public async Task<bool> IsStatTilePresentAsync(string labelText)
    {
        return await _page.EvaluateAsync<bool>(@"(labelText) => {
            const stats = document.querySelectorAll('.cockpit-stat');
            for (const s of stats) {
                const label = s.querySelector('.cockpit-stat-l');
                if (label && label.textContent.trim() === labelText) return true;
            }
            return false;
        }", labelText);
    }

    /// <summary>
    /// Returns the href of the issue link chip in the "Active work" cell for the given agent,
    /// or null if no issue link is present.
    /// </summary>
    public async Task<string?> GetActiveIssueLinkAsync(string agentId)
    {
        return await _page.EvaluateAsync<string?>(@"(agentId) => {
            const rows = document.querySelectorAll('.monitoring-table tbody tr');
            for (const row of rows) {
                const mono = row.querySelector('.monitoring-mono');
                if (mono && mono.textContent.trim() === agentId) {
                    const cell = row.querySelector('.fleet-work-cell');
                    if (!cell) return null;
                    const issueLink = cell.querySelector('a[title^=""Open issue""]');
                    return issueLink ? issueLink.getAttribute('href') : null;
                }
            }
            return null;
        }", agentId);
    }

    /// <summary>
    /// Returns the href of the run link in the "Active work" cell for the given agent,
    /// or null if no run link is present.
    /// </summary>
    public async Task<string?> GetActiveRunLinkAsync(string agentId)
    {
        return await _page.EvaluateAsync<string?>(@"(agentId) => {
            const rows = document.querySelectorAll('.monitoring-table tbody tr');
            for (const row of rows) {
                const mono = row.querySelector('.monitoring-mono');
                if (mono && mono.textContent.trim() === agentId) {
                    const cell = row.querySelector('.fleet-work-cell');
                    if (!cell) return null;
                    const runLink = cell.querySelector('a[title=""Open pipeline run""]');
                    return runLink ? runLink.getAttribute('href') : null;
                }
            }
            return null;
        }", agentId);
    }

    /// <summary>
    /// Returns the href of the PR link in the "Active work" cell for the given agent,
    /// or null if no PR link is present.
    /// </summary>
    public async Task<string?> GetActivePrLinkAsync(string agentId)
    {
        return await _page.EvaluateAsync<string?>(@"(agentId) => {
            const rows = document.querySelectorAll('.monitoring-table tbody tr');
            for (const row of rows) {
                const mono = row.querySelector('.monitoring-mono');
                if (mono && mono.textContent.trim() === agentId) {
                    const cell = row.querySelector('.fleet-work-cell');
                    if (!cell) return null;
                    const prLink = cell.querySelector('a[title=""Open pull request""]');
                    return prLink ? prLink.getAttribute('href') : null;
                }
            }
            return null;
        }", agentId);
    }

    /// <summary>Polls until the agent shows the expected status, or the timeout elapses.</summary>
    public async Task WaitForAgentStatusAsync(string agentId, string expectedStatus, int timeoutMs = 15_000)
    {
        await _page.WaitForFunctionAsync(@"(args) => {
            const [agentId, expectedStatus] = args;
            const rows = document.querySelectorAll('.monitoring-table tbody tr');
            for (const row of rows) {
                const mono = row.querySelector('.monitoring-mono');
                if (mono && mono.textContent.trim() === agentId) {
                    const statusSpan = row.querySelector('.monitoring-status');
                    return statusSpan ? statusSpan.textContent.trim().includes(expectedStatus) : false;
                }
            }
            return false;
        }", new object[] { agentId, expectedStatus }, new() { Timeout = timeoutMs });
    }
}
