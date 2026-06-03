using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Polly;
using Serilog;


namespace CodingAgentWebUI.Agent;

/// <summary>
/// Wraps SignalR hub invocations so <see cref="LocalPipelineExecutor"/> can call
/// issue operations without knowing about SignalR.
/// Implements <see cref="IAgentIssueOperations"/> so existing orchestrators
/// can be reused on the agent.
/// </summary>
public sealed class OrchestratorProxy : IAgentIssueOperations
{
    private readonly HubConnection _connection;
    private readonly string _jobId;
    private readonly ResiliencePipeline _signalRPipeline;

    public OrchestratorProxy(HubConnection connection, string jobId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(jobId);

        _connection = connection;
        _jobId = jobId;
        _signalRPipeline = ResiliencePipelineFactory.CreateSignalRPipeline(Log.Logger);
    }

    /// <summary>
    /// Posts an analysis comment on the issue via the orchestrator.
    /// </summary>
    public Task PostCommentAsync(string issueIdentifier, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(body);
        return _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync(
                "RequestPostComment",
                _jobId,
                CommentType.Analysis,
                new CommentPayload { AnalysisMarkdown = body },
                token), ct).AsTask();
    }

    /// <summary>
    /// Swaps the agent label on the issue via the orchestrator.
    /// </summary>
    public Task SwapLabelAsync(string issueIdentifier, string newLabel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(newLabel);
        return _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync("RequestLabelChange", _jobId, newLabel, (int)LabelTargetKind.Issue, token), ct).AsTask();
    }

    /// <summary>
    /// Swaps the agent label via the orchestrator with explicit target kind routing.
    /// Used by review runs to route label swaps to PRs instead of issues.
    /// </summary>
    public Task SwapLabelAsync(string identifier, string newLabel, LabelTargetKind targetKind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(newLabel);
        return _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync("RequestLabelChange", _jobId, newLabel, (int)targetKind, token), ct).AsTask();
    }

    /// <summary>
    /// Posts a gate rejection comment (not_ready assessment) via the orchestrator.
    /// </summary>
    public Task PostGateRejectionAsync(string assessmentJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assessmentJson);
        return _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync(
                "RequestPostComment",
                _jobId,
                CommentType.GateRejection,
                new CommentPayload { AssessmentJson = assessmentJson },
                token), ct).AsTask();
    }

    /// <summary>
    /// Posts a gate wont-do comment via the orchestrator.
    /// </summary>
    public Task PostGateWontDoAsync(string assessmentJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assessmentJson);
        return _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync(
                "RequestPostComment",
                _jobId,
                CommentType.GateWontDo,
                new CommentPayload { AssessmentJson = assessmentJson },
                token), ct).AsTask();
    }

    /// <summary>
    /// Requests a fresh short-lived token from the orchestrator when the current one expires.
    /// </summary>
    public async Task<string> RequestTokenRefreshAsync(ProviderKind kind, CancellationToken ct)
    {
        var response = await _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync<TokenRefreshResponse>(
                "RequestTokenRefresh", _jobId, kind, token), ct);
        return response.Token;
    }

    // --- Decomposition-specific operations (proxied to orchestrator via SignalR) ---

    /// <summary>
    /// Creates a new issue via the orchestrator. Returns the created issue's identifier and URL.
    /// </summary>
    public async Task<CreatedIssueResult> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(labels);

        return await _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync<CreatedIssueResult>(
                "RequestCreateIssue", _jobId, title, body, labels, token), ct);
    }

    /// <summary>
    /// Creates a new issue via a specific issue provider (for cross-repo routing).
    /// Routes through the orchestrator which resolves the provider from the config ID.
    /// </summary>
    public async Task<CreatedIssueResult> CreateIssueForProviderAsync(
        string issueProviderConfigId, string title, string body, IReadOnlyList<string> labels, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueProviderConfigId);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(labels);

        return await _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync<CreatedIssueResult>(
                "RequestCreateIssueForProvider", _jobId, issueProviderConfigId, title, body, labels, token), ct);
    }

    /// <summary>
    /// Lists open issues with optional label filtering via the orchestrator.
    /// </summary>
    public async Task<PagedResult<IssueSummary>> ListOpenIssuesAsync(int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        return await _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync<PagedResult<IssueSummary>>(
                "RequestListOpenIssues", _jobId, page, pageSize, labels, token), ct);
    }

    /// <summary>
    /// Gets full issue details by identifier via the orchestrator.
    /// </summary>
    public async Task<IssueDetail> GetIssueAsync(string identifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        return await _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync<IssueDetail>(
                "RequestGetIssue", _jobId, identifier, token), ct);
    }

    /// <summary>
    /// Lists all comments on an issue via the orchestrator.
    /// </summary>
    public async Task<IReadOnlyList<IssueComment>> ListCommentsAsync(string identifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        return await _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync<IReadOnlyList<IssueComment>>(
                "RequestListComments", _jobId, identifier, token), ct);
    }

    /// <summary>
    /// Updates an existing comment by ID via the orchestrator.
    /// </summary>
    public Task UpdateCommentAsync(string issueIdentifier, string commentId, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(commentId);
        ArgumentNullException.ThrowIfNull(body);

        return _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync(
                "RequestUpdateComment", _jobId, issueIdentifier, commentId, body, token), ct).AsTask();
    }
}
