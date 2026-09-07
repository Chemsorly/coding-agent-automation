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

    // ── Proactive token renewal cache ────────────────────────────────────
    // Mirrors the 5-minute renewal buffer used by GitHubAppAuthService on the server side.
    // Keyed by ProviderKind so the repo and brain tokens are cached independently.
    private static readonly TimeSpan TokenRenewalBuffer = TimeSpan.FromMinutes(5);
    private readonly Dictionary<ProviderKind, (string Token, DateTimeOffset ExpiresAt)> _tokenCache = new();
    // TODO [WARNING]: _tokenCacheLock is never disposed. OrchestratorProxy does not implement IDisposable/IAsyncDisposable,
    // so the SemaphoreSlim's underlying WaitHandle leaks on each job. Implement IDisposable and call
    // _tokenCacheLock.Dispose(), then update LocalConsolidationExecutor and LocalPipelineExecutor to dispose the proxy.
    private readonly SemaphoreSlim _tokenCacheLock = new(1, 1);

    /// <summary>
    /// Test-only delegate for the hub token-refresh call. When non-null (injected via the
    /// internal test constructor), this replaces the live SignalR invocation in
    /// <see cref="RequestTokenRefreshAsync"/> so the caching and renewal logic can be exercised
    /// without a started hub connection.
    /// </summary>
    private readonly Func<ProviderKind, CancellationToken, Task<TokenRefreshResponse>>? _tokenRefreshDelegate;

    public OrchestratorProxy(HubConnection connection, string jobId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(jobId);

        _connection = connection;
        _jobId = jobId;
        _signalRPipeline = ResiliencePipelineFactory.CreateSignalRPipeline(Log.Logger);
        _tokenRefreshDelegate = null;
    }

    /// <summary>
    /// Test-only constructor that injects a token-refresh delegate instead of a live hub
    /// connection. Allows unit tests to exercise the caching and proactive-renewal logic
    /// without needing a started <see cref="HubConnection"/>.
    /// </summary>
    internal OrchestratorProxy(
        HubConnection connection,
        string jobId,
        Func<ProviderKind, CancellationToken, Task<TokenRefreshResponse>> tokenRefreshDelegate)
        : this(connection, jobId)
    {
        _tokenRefreshDelegate = tokenRefreshDelegate;
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
    /// Caches the returned token and proactively renews it when within
    /// <see cref="TokenRenewalBuffer"/> of expiry, mirroring the server-side
    /// <c>GitHubAppAuthService</c> renewal buffer.
    /// </summary>
    public async Task<string> RequestTokenRefreshAsync(ProviderKind kind, CancellationToken ct)
    {
        // Fast path: check under lock whether the cached token is still fresh.
        await _tokenCacheLock.WaitAsync(ct);
        try
        {
            if (_tokenCache.TryGetValue(kind, out var cached))
            {
                var remainingLife = cached.ExpiresAt - DateTimeOffset.UtcNow;
                if (remainingLife > TokenRenewalBuffer)
                    return cached.Token;
            }
        }
        finally
        {
            _tokenCacheLock.Release();
        }

        // Slow path: ask the orchestrator for a fresh token.
        // TODO [WARNING]: TOCTOU thundering-herd — two concurrent callers for the same ProviderKind
        // that both observe a cache miss here will both invoke the hub, both mint tokens, and the last
        // write to _tokenCache wins. Outcome is functionally correct but wastes SignalR round-trips and
        // GitHub App quota. Fix: use a single-flight pattern (cache Task<TokenRefreshResponse> under
        // the lock) so concurrent callers await the same in-flight request.
        TokenRefreshResponse response;
        if (_tokenRefreshDelegate is not null)
        {
            // Test path: use the injected delegate instead of the live hub connection.
            response = await _tokenRefreshDelegate(kind, ct);
        }
        else
        {
            response = await _signalRPipeline.ExecuteAsync(async token =>
                await _connection.InvokeAsync<TokenRefreshResponse>(
                    HubMethodNames.RequestTokenRefresh, _jobId, kind, token), ct);
        }

        // Cache the result so subsequent calls within the token lifetime are free.
        await _tokenCacheLock.WaitAsync(ct);
        try
        {
            _tokenCache[kind] = (response.Token, response.ExpiresAt);
        }
        finally
        {
            _tokenCacheLock.Release();
        }

        return response.Token;
    }

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
