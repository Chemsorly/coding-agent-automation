using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.PageObjects;

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
