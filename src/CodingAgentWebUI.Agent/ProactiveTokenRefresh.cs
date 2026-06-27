using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Provides proactive token refresh before git push operations in K8s mode.
/// If the token age exceeds 45 minutes, requests a fresh token via SignalR.
/// On failure after 3 retries with exponential backoff, posts Failed status
/// with FailureReason=TokenRefreshFailure.
/// </summary>
public sealed class ProactiveTokenRefresh
{
    private readonly HubConnection _connection;
    private readonly string _jobId;
    private readonly WorkItemHttpClient? _workItemClient;
    private readonly string? _workItemId;
    private readonly string? _agentId;
    private readonly Serilog.ILogger _logger;

    private long _lastTokenTimeTicks = DateTimeOffset.UtcNow.Ticks;
    private static readonly TimeSpan TokenMaxAge = TimeSpan.FromMinutes(45);
    private static readonly int[] RetryDelaysSeconds = [2, 4, 8]; // 3 retries with exponential backoff

    public ProactiveTokenRefresh(
        HubConnection connection,
        string jobId,
        WorkItemHttpClient? workItemClient,
        string? workItemId,
        string? agentId,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _jobId = jobId;
        _workItemClient = workItemClient;
        _workItemId = workItemId;
        _agentId = agentId;
        _logger = logger;
    }

    /// <summary>
    /// Records that a fresh token was obtained (resets the age timer).
    /// </summary>
    public void MarkTokenRefreshed()
    {
        Interlocked.Exchange(ref _lastTokenTimeTicks, DateTimeOffset.UtcNow.Ticks);
    }

    /// <summary>
    /// Checks if a token refresh is needed before a git push operation.
    /// If the token is older than 45 minutes, requests a fresh one via SignalR.
    /// </summary>
    /// <returns>The fresh token if refreshed, or null if no refresh was needed.</returns>
    /// <exception cref="TokenRefreshFailureException">
    /// Thrown when all refresh retries fail. The caller should treat this as fatal.
    /// </exception>
    public async Task<string?> EnsureFreshTokenAsync(ProviderKind kind, CancellationToken ct)
    {
        var lastTicks = Interlocked.Read(ref _lastTokenTimeTicks);
        var tokenAge = DateTimeOffset.UtcNow - new DateTimeOffset(lastTicks, TimeSpan.Zero);
        if (tokenAge < TokenMaxAge)
        {
            return null; // Token is fresh enough
        }

        _logger.Information("Token age {TokenAge:F0}min exceeds {MaxAge}min threshold, requesting refresh for {Kind}",
            tokenAge.TotalMinutes, TokenMaxAge.TotalMinutes, kind);

        for (var attempt = 0; attempt <= RetryDelaysSeconds.Length; attempt++)
        {
            try
            {
                var response = await _connection.InvokeAsync<TokenRefreshResponse>(
                    HubMethodNames.RequestTokenRefresh, _jobId, kind, ct);

                Interlocked.Exchange(ref _lastTokenTimeTicks, DateTimeOffset.UtcNow.Ticks);
                _logger.Information("Token refreshed successfully for {Kind}", kind);
                return response.Token;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < RetryDelaysSeconds.Length)
            {
                _logger.Warning(ex, "Token refresh attempt {Attempt}/{Max} failed for {Kind}",
                    attempt + 1, RetryDelaysSeconds.Length + 1, kind);
                await Task.Delay(TimeSpan.FromSeconds(RetryDelaysSeconds[attempt]), ct);
            }
            catch (Exception ex)
            {
                // Final attempt failed — post Failed status if in K8s mode
                _logger.Error(ex, "All token refresh attempts exhausted for {Kind}", kind);

                if (_workItemClient is not null && _workItemId is not null)
                {
                    try
                    {
                        var failedUpdate = new WorkItemStatusUpdate
                        {
                            Status = "Failed",
                            AgentId = _agentId,
                            ErrorMessage = $"Token refresh failed for {kind} after {RetryDelaysSeconds.Length + 1} attempts: {ex.Message}",
                            FailureReason = nameof(Pipeline.Models.FailureReason.TokenRefreshFailure)
                        };
                        await _workItemClient.PostStatusAsync(_workItemId, failedUpdate, CancellationToken.None);
                    }
                    catch (Exception postEx)
                    {
                        _logger.Warning(postEx, "Failed to POST TokenRefreshFailure status");
                    }
                }

                throw new TokenRefreshFailureException(
                    $"Token refresh failed for {kind} after all retries", ex);
            }
        }

        // Should not reach here, but defensive
        throw new TokenRefreshFailureException("Token refresh failed unexpectedly");
    }
}

/// <summary>
/// Thrown when proactive token refresh fails after all retries.
/// Indicates the pipeline should abort with FailureReason=TokenRefreshFailure.
/// </summary>
public sealed class TokenRefreshFailureException : Exception
{
    public TokenRefreshFailureException(string message) : base(message) { }
    public TokenRefreshFailureException(string message, Exception inner) : base(message, inner) { }
}
