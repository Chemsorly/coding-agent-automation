using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.PageObjects;

/// <summary>
/// Page object for the /agent-monitoring page.
/// Encapsulates navigation and queries for agent status, active runs, and run history.
/// The monitoring page uses CSS classes (not data-testid) for element identification:
/// - Agent rows: tr.monitoring-row-{status} (idle, busy, disconnected)
/// - Agent status: span.monitoring-status.monitoring-status-{status}
/// - Agent ID: td.monitoring-mono (first column in agent table)
/// - Active runs: section header "🔄 Active Runs (N)"
/// - History: section header "📜 Recent Runs (N)"
/// </summary>
public sealed class AgentMonitoringPage
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    public AgentMonitoringPage(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>Navigates to the /agent-monitoring page and waits for it to render.</summary>
    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/agent-monitoring");

        // Wait for the page header to render
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });

        // Allow time for the Blazor Server circuit to connect and data to load
        await _page.WaitForTimeoutAsync(3000);
    }

    /// <summary>
    /// Gets the status text for a specific agent (e.g., "Idle", "Busy", "Disconnected").
    /// Returns null if the agent is not found on the page.
    /// </summary>
    public async Task<string?> GetAgentStatusAsync(string agentId)
    {
        // Find the agent row by looking for the agent ID in a monitoring-mono cell,
        // then get the status span from the same row.
        var statusText = await _page.EvaluateAsync<string?>(@"(agentId) => {
            const rows = document.querySelectorAll('.monitoring-table tbody tr');
            for (const row of rows) {
                const monoCell = row.querySelector('td.monitoring-mono');
                if (monoCell && monoCell.textContent.trim() === agentId) {
                    const statusSpan = row.querySelector('.monitoring-status');
                    if (statusSpan) {
                        // Extract just the status text (remove emoji prefix)
                        // Status text format: '<emoji> Idle' or '<emoji> Busy' etc.
                        const text = statusSpan.textContent.trim();
                        const parts = text.split(' ');
                        return parts.length > 1 ? parts.slice(1).join(' ') : text;
                    }
                }
            }
            return null;
        }", agentId);

        return statusText;
    }

    /// <summary>
    /// Checks whether an agent card/row is visible on the monitoring page.
    /// </summary>
    public async Task<bool> IsAgentVisibleAsync(string agentId)
    {
        var isVisible = await _page.EvaluateAsync<bool>(@"(agentId) => {
            const rows = document.querySelectorAll('.monitoring-table tbody tr');
            for (const row of rows) {
                const monoCell = row.querySelector('td.monitoring-mono');
                if (monoCell && monoCell.textContent.trim() === agentId) {
                    return true;
                }
            }
            return false;
        }", agentId);

        return isVisible;
    }

    /// <summary>
    /// Gets the number of active runs displayed on the page.
    /// Parses the count from the "🔄 Active Runs (N)" section header.
    /// </summary>
    public async Task<int> GetActiveRunCountAsync()
    {
        var count = await _page.EvaluateAsync<int>(@"() => {
            const headers = document.querySelectorAll('.settings-section h2');
            for (const h of headers) {
                const text = h.textContent || '';
                const match = text.match(/Active Runs\s*\((\d+)\)/);
                if (match) return parseInt(match[1], 10);
            }
            return 0;
        }");

        return count;
    }

    /// <summary>
    /// Gets the number of completed runs in the history section.
    /// Parses the count from the "📜 Recent Runs (N)" section header.
    /// </summary>
    public async Task<int> GetRecentRunCountAsync()
    {
        var count = await _page.EvaluateAsync<int>(@"() => {
            const headers = document.querySelectorAll('.settings-section h2');
            for (const h of headers) {
                const text = h.textContent || '';
                const match = text.match(/Recent Runs\s*\((\d+)\)/);
                if (match) return parseInt(match[1], 10);
            }
            return 0;
        }");

        return count;
    }

    /// <summary>
    /// Gets the total number of registered agents displayed on the page.
    /// Parses the count from the "🤖 Registered Agents (N)" section header.
    /// </summary>
    public async Task<int> GetRegisteredAgentCountAsync()
    {
        var count = await _page.EvaluateAsync<int>(@"() => {
            const headers = document.querySelectorAll('.settings-section h2');
            for (const h of headers) {
                const text = h.textContent || '';
                const match = text.match(/Registered Agents\s*\((\d+)\)/);
                if (match) return parseInt(match[1], 10);
            }
            return 0;
        }");

        return count;
    }

    /// <summary>
    /// Waits for a specific agent to appear on the monitoring page with the expected status.
    /// Polls the page every 500ms until the agent is found or timeout is reached.
    /// </summary>
    public async Task WaitForAgentStatusAsync(string agentId, string expectedStatus, int timeoutMs = 10_000)
    {
        await _page.WaitForFunctionAsync(@"(args) => {
            const [agentId, expectedStatus] = args;
            const rows = document.querySelectorAll('.monitoring-table tbody tr');
            for (const row of rows) {
                const monoCell = row.querySelector('td.monitoring-mono');
                if (monoCell && monoCell.textContent.trim() === agentId) {
                    const statusSpan = row.querySelector('.monitoring-status');
                    if (statusSpan) {
                        const text = statusSpan.textContent.trim();
                        return text.includes(expectedStatus);
                    }
                }
            }
            return false;
        }", new object[] { agentId, expectedStatus }, new() { Timeout = timeoutMs });
    }
}
