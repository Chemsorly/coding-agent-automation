namespace CodingAgent.Web.Ui;

/// <summary>
/// Manual dispatch from the AgentCoding drawers (spec 051 2.3c). A request names the template and the
/// issue or pull request; the API reads it from the template's providers, needs operator on the
/// template's project and enqueues the work. Each call returns the message to show. A refusal, or a
/// provider that is gone or failed, throws <see cref="InvalidOperationException"/> with the reason.
/// </summary>
public interface IUiDispatchClient
{
    /// <summary>Dispatches an issue of the template's tracker for implementation.</summary>
    Task<string> DispatchIssueAsync(string templateId, string issueIdentifier, CancellationToken ct = default);

    /// <summary>Dispatches an open pull request of the template's repository for review.</summary>
    Task<string> DispatchPullRequestAsync(string templateId, int number, CancellationToken ct = default);

    /// <summary>Dispatches an epic of the template's epic tracker for analysis or decomposition.</summary>
    Task<string> DispatchEpicAsync(string templateId, string issueIdentifier, CancellationToken ct = default);
}

public sealed class UiDispatchClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiDispatchClient
{
    public Task<string> DispatchIssueAsync(string templateId, string issueIdentifier, CancellationToken ct = default) =>
        PostAsync($"{Template(templateId)}/issues/{Uri.EscapeDataString(issueIdentifier)}/dispatch", ct);

    public Task<string> DispatchPullRequestAsync(string templateId, int number, CancellationToken ct = default) =>
        PostAsync($"{Template(templateId)}/pull-requests/{number}/dispatch", ct);

    public Task<string> DispatchEpicAsync(string templateId, string issueIdentifier, CancellationToken ct = default) =>
        PostAsync($"{Template(templateId)}/epics/{Uri.EscapeDataString(issueIdentifier)}/dispatch", ct);

    private Task<string> PostAsync(string path, CancellationToken ct) =>
        SendForResultAsync(HttpMethod.Post, path, null, UiJsonContext.Default.String, ct);

    private static string Template(string templateId) => $"/api/ui/templates/{Uri.EscapeDataString(templateId)}";
}
