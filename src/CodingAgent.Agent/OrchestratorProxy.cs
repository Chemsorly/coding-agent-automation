using CodingAgent.Infrastructure.Resilience;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Polly;
using Serilog;


namespace CodingAgent.Agent;

/// <summary>
/// Wraps SignalR hub invocations so <see cref="LocalPipelineExecutor"/> can call
/// issue operations without knowing about SignalR.
/// Implements <see cref="IAgentIssueOperations"/> so existing orchestrators
/// can be reused on the agent.
/// </summary>
public sealed class OrchestratorProxy : IAgentIssueOperations, IDisposable
{
    private readonly HubConnection _connection;
    private readonly string _jobId;
    private readonly ResiliencePipeline _signalRPipeline;

    // ── Proactive token renewal cache ────────────────────────────────────
    // Keyed by ProviderKind so the repo and brain tokens are cached independently.
    private static readonly TimeSpan TokenRenewalBuffer = TokenRefreshConstants.RenewalBuffer;
    private readonly Dictionary<ProviderKind, (string Token, DateTimeOffset ExpiresAt)> _tokenCache = new();
    private readonly SemaphoreSlim _tokenCacheLock = new(1, 1);

    // Single-flight: stores the in-flight Task per ProviderKind so concurrent callers
    // await the same hub invocation rather than each issuing their own.
    // Entry is removed in a `finally` block so a faulted task is never re-used.
    private readonly Dictionary<ProviderKind, Task<TokenRefreshResponse>> _tokenInflight = new();

    private bool _disposed;

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
    /// Disposes the semaphore slim used for token cache locking.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tokenCacheLock.Dispose();
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
    /// Uses a single-flight pattern so concurrent callers for the same <paramref name="kind"/>
    /// await the same in-flight hub invocation rather than each issuing their own.
    /// </summary>
    public async Task<string> RequestTokenRefreshAsync(ProviderKind kind, CancellationToken ct)
    {
        // Fast path: check under lock whether the cached token is still fresh,
        // or whether there is already an in-flight request we can piggyback on.
        Task<TokenRefreshResponse>? inflight = null;

        await _tokenCacheLock.WaitAsync(ct);
        try
        {
            if (_tokenCache.TryGetValue(kind, out var cached))
            {
                var remainingLife = cached.ExpiresAt - DateTimeOffset.UtcNow;
                if (remainingLife > TokenRenewalBuffer)
                    return cached.Token;
            }

            // Single-flight: piggyback on an existing in-flight request if present
            if (_tokenInflight.TryGetValue(kind, out inflight))
            {
                // Fall through — await inflight outside the lock
            }
            else
            {
                // Start a new request and register it as the in-flight task
                inflight = FetchTokenFromHubAsync(kind, ct);
                _tokenInflight[kind] = inflight;
            }
        }
        finally
        {
            _tokenCacheLock.Release();
        }

        // Await outside the lock — multiple callers may be awaiting the same task
        TokenRefreshResponse response;
        try
        {
            // TODO [WARNING]: Single-flight cancellation propagation — the in-flight task was started
            // with the first caller's `ct`. If that caller's token is cancelled mid-flight, the shared
            // task will fault and all concurrent waiters (whose own tokens are still live) receive an
            // OperationCanceledException. The `finally` block below correctly removes the faulted entry
            // so later callers retry, but innocent concurrent callers get propagated cancellation.
            // This is a known single-flight trade-off; document it in code reviews if it surfaces.
            // (Correctness Review / .NET Specialist)
            response = await inflight;
        }
        finally
        {
            // Remove the in-flight entry regardless of outcome so a faulted task is never reused
            // TODO [WARNING]: The two _tokenCacheLock.WaitAsync(CancellationToken.None) calls below (here
            // and at the cache-write step) do not guard against a concurrent Dispose(). If Dispose() runs
            // while a token refresh is in-flight, _tokenCacheLock is disposed and WaitAsync throws
            // ObjectDisposedException. Add a _disposed check before each WaitAsync if concurrent
            // disposal during active token refresh is a realistic scenario. (Correctness Review / .NET Specialist)
            await _tokenCacheLock.WaitAsync(CancellationToken.None);
            try
            {
                if (_tokenInflight.TryGetValue(kind, out var current) && ReferenceEquals(current, inflight))
                    _tokenInflight.Remove(kind);
            }
            finally
            {
                _tokenCacheLock.Release();
            }
        }

        // Cache the successful result
        await _tokenCacheLock.WaitAsync(CancellationToken.None);
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

    // Maximum number of retry attempts for transient (non-HubException) token-vend failures.
    // HubException always signals a permanent server-side condition (missing config, no auth method,
    // stale token) and must NOT be retried here — AgentTokenRefreshService's own TODO notes this.
    // This retry is intentionally narrow: only non-HubException errors (network blips, connection
    // state transitions) are retried. The existing _signalRPipeline already handles those too, but
    // a transient blip that exhausts _signalRPipeline retries would otherwise fail the whole run.
    private const int TokenVendMaxRetries = 2;

    private async Task<TokenRefreshResponse> FetchTokenFromHubAsync(ProviderKind kind, CancellationToken ct)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt <= TokenVendMaxRetries; attempt++)
        {
            try
            {
                if (_tokenRefreshDelegate is not null)
                    return await _tokenRefreshDelegate(kind, ct);

                return await _signalRPipeline.ExecuteAsync(async token =>
                    await _connection.InvokeAsync<TokenRefreshResponse>(
                        HubMethodNames.RequestTokenRefresh, _jobId, kind, token), ct);
            }
            catch (HubException)
            {
                // HubException = permanent server-side failure (bad config, no auth method,
                // expired pre-vended token). Never retry — surface immediately.
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancelled — propagate immediately, do not retry.
                throw;
            }
            catch (Exception ex) when (attempt < TokenVendMaxRetries)
            {
                // Transient network/connection error — log and retry.
                lastException = ex;
                Log.Warning(ex,
                    "Token-vend attempt {Attempt}/{Max} failed for job {JobId} (kind: {Kind}) — retrying",
                    attempt + 1, TokenVendMaxRetries + 1, _jobId, kind);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (1 << attempt)), ct);
            }
        }

        // All retries exhausted — rethrow the last transient exception.
        throw lastException!;
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
