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
    public Task<string?> PostCommentAsync(IssueIdentifier issueIdentifier, string body, CancellationToken ct)
    {
        // TODO: ThrowIfNullOrEmpty(issueIdentifier.Value) relies on .NET 8+ behavior where null is handled gracefully.
        // If the project ever targets a lower TFM, this pattern (repeated across OrchestratorProxy, GitHubIssueProvider,
        // GitLabIssueProvider, LabelService) would throw NullReferenceException for default(IssueIdentifier).
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value);
        ArgumentNullException.ThrowIfNull(body);
        return _signalRPipeline.ExecuteAsync(async token =>
        {
            await _connection.InvokeAsync(
                HubMethodNames.RequestPostComment,
                _jobId,
                CommentType.Analysis,
                new CommentPayload { AnalysisMarkdown = body },
                token);
            return (string?)null;
        }, ct).AsTask();
    }

    /// <summary>
    /// Swaps the agent label on the issue via the orchestrator.
    /// </summary>
    public Task SwapLabelAsync(IssueIdentifier issueIdentifier, string newLabel, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value);
        ArgumentNullException.ThrowIfNull(newLabel);
        return _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync(HubMethodNames.RequestLabelChange, _jobId, newLabel, (int)LabelTargetKind.Issue, token), ct).AsTask();
    }

    /// <summary>
    /// Swaps the agent label via the orchestrator with explicit target kind routing.
    /// Used by review runs to route label swaps to PRs instead of issues.
    /// </summary>
    public Task SwapLabelAsync(IssueIdentifier identifier, string newLabel, LabelTargetKind targetKind, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        ArgumentNullException.ThrowIfNull(newLabel);
        return _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync(HubMethodNames.RequestLabelChange, _jobId, newLabel, (int)targetKind, token), ct).AsTask();
    }

    /// <summary>
    /// Posts a gate rejection comment (not_ready assessment) via the orchestrator.
    /// </summary>
    public Task PostGateRejectionAsync(string assessmentJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assessmentJson);
        return _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync(
                HubMethodNames.RequestPostComment,
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
                HubMethodNames.RequestPostComment,
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
                HubMethodNames.RequestTokenRefresh, _jobId, kind, token), ct);
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
                HubMethodNames.RequestCreateIssue, _jobId, title, body, labels, token), ct);
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
                HubMethodNames.RequestCreateIssueForProvider, _jobId, issueProviderConfigId, title, body, labels, token), ct);
    }

    /// <summary>
    /// Lists open issues with optional label filtering via the orchestrator.
    /// </summary>
    public async Task<PagedResult<IssueSummary>> ListOpenIssuesAsync(int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        return await _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync<PagedResult<IssueSummary>>(
                HubMethodNames.RequestListOpenIssues, _jobId, page, pageSize, labels, token), ct);
    }

    /// <summary>
    /// Lists closed issues with optional label filtering and date cutoff via the orchestrator.
    /// Used during decomposition runs to include recently-closed sibling issues in agent context.
    /// </summary>
    public async Task<PagedResult<IssueSummary>> ListClosedIssuesAsync(int page, int pageSize, IReadOnlyList<string>? labels, DateTime? since, CancellationToken ct)
    {
        return await _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync<PagedResult<IssueSummary>>(
                HubMethodNames.RequestListClosedIssues, _jobId, page, pageSize, labels, since, token), ct);
    }

    /// <summary>
    /// Gets full issue details by identifier via the orchestrator.
    /// </summary>
    public async Task<IssueDetail> GetIssueAsync(IssueIdentifier identifier, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);

        return await _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync<IssueDetail>(
                HubMethodNames.RequestGetIssue, _jobId, identifier, token), ct);
    }

    /// <summary>
    /// Lists all comments on an issue via the orchestrator.
    /// </summary>
    public async Task<IReadOnlyList<IssueComment>> ListCommentsAsync(IssueIdentifier identifier, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);

        return await _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync<IReadOnlyList<IssueComment>>(
                HubMethodNames.RequestListComments, _jobId, identifier, token), ct);
    }

    /// <summary>
    /// Updates an existing comment by ID via the orchestrator.
    /// </summary>
    public Task UpdateCommentAsync(IssueIdentifier issueIdentifier, string commentId, string body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value);
        ArgumentNullException.ThrowIfNull(commentId);
        ArgumentNullException.ThrowIfNull(body);

        return _signalRPipeline.ExecuteAsync(async token =>
            await _connection.InvokeAsync(
                HubMethodNames.RequestUpdateComment, _jobId, issueIdentifier, commentId, body, token), ct).AsTask();
    }
}
