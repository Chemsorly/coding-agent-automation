using Octokit;
using Polly;
using Serilog;
using CodingAgent.Infrastructure.Resilience;
using PipelineRateLimitExceededException = CodingAgent.Pipeline.Models.RateLimitExceededException;

namespace CodingAgent.Infrastructure.GitHub;

/// <summary>
/// Base class for GitHub providers that share common authentication,
/// client management, and repository validation patterns.
/// </summary>
public abstract class GitHubProviderBase : IAsyncDisposable
{
    private readonly GitHubClientProvider _clientProvider;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly TimeProvider _timeProvider;

    /// <summary>Repository owner.</summary>
    protected string Owner { get; }

    /// <summary>Repository name.</summary>
    protected string Repo { get; }

    protected GitHubProviderBase(GitHubConnectionInfo connection, string token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(token);
        _clientProvider = new GitHubClientProvider(connection.ApiUrl, token);
        Owner = connection.Owner;
        Repo = connection.Repo;
        _resiliencePipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(Log.Logger);
        _timeProvider = TimeProvider.System;
    }

    protected GitHubProviderBase(GitHubConnectionInfo connection, Func<CancellationToken, Task<string>> tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _clientProvider = new GitHubClientProvider(connection.ApiUrl, tokenProvider);
        Owner = connection.Owner;
        Repo = connection.Repo;
        _resiliencePipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(Log.Logger);
        _timeProvider = TimeProvider.System;
    }

    protected GitHubProviderBase(GitHubConnectionInfo connection, IGitHubClient client)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(client);
        _clientProvider = new GitHubClientProvider(client);
        Owner = connection.Owner;
        Repo = connection.Repo;
        _resiliencePipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(Log.Logger);
        _timeProvider = TimeProvider.System;
    }

    protected GitHubProviderBase(GitHubConnectionInfo connection, IGitHubClient client, string token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(token);
        _clientProvider = new GitHubClientProvider(client, token);
        Owner = connection.Owner;
        Repo = connection.Repo;
        _resiliencePipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(Log.Logger);
        _timeProvider = TimeProvider.System;
    }

    /// <summary>API base URL from the client provider.</summary>
    protected string? ApiUrl => _clientProvider.ApiUrl;

    /// <summary>
    /// Derives the GraphQL endpoint URI from the configured API URL.
    /// For GitHub.com (api.github.com) → https://api.github.com/graphql.
    /// For GHE (e.g. https://github.example.com/api/v3) → https://github.example.com/api/graphql.
    /// </summary>
    protected Uri DeriveGraphQlUri()
    {
        var apiUrl = ApiUrl ?? "https://api.github.com";

        if (apiUrl.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(apiUrl[..^"/api/v3".Length] + "/api/graphql");
        }

        return new Uri(apiUrl.TrimEnd('/') + "/graphql");
    }

    /// <summary>Returns a GitHubClient configured with a current token.</summary>
    protected Task<IGitHubClient> GetClientAsync(CancellationToken ct)
        => _clientProvider.GetClientAsync(ct);

    /// <summary>Returns a current token.</summary>
    protected Task<string> GetTokenAsync(CancellationToken ct)
        => _clientProvider.GetTokenAsync(ct);

    /// <inheritdoc />
    public virtual async Task ValidateAsync(CancellationToken ct)
    {
        await ExecuteWithResilienceAsync(
            async client => { await client.Repository.Get(Owner, Repo); return true; },
            "ValidateRepository", ct);
    }

    /// <summary>
    /// Parses a string issue identifier into a numeric issue number.
    /// </summary>
    protected static int ParseIssueIdentifier(string identifier)
    {
        if (!int.TryParse(identifier, out var issueNumber))
        {
            Log.Warning("Invalid issue identifier '{Identifier}' — expected numeric issue number", identifier);
            throw new ArgumentException(
                $"Invalid issue identifier: '{identifier}'. Expected a numeric issue number.",
                nameof(identifier));
        }
        return issueNumber;
    }

    private static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromSeconds(60);

    private const string RateLimitLogMessage = "GitHub API rate limit exceeded, reset at {Reset}";

    /// <summary>
    /// Executes an Octokit API call with resilience (retry on transient errors) and rate limit handling.
    /// Acquires a fresh client inside the retry loop to ensure token freshness on retry.
    /// </summary>
    /// <param name="operation">The API operation to execute.</param>
    /// <param name="operationName">Name tag for <c>github.api.requests</c> metric.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="isGraphQL">
    /// Pass <c>true</c> for lambdas that make GraphQL mutations (e.g. <c>markPullRequestReadyForReview</c>,
    /// <c>convertPullRequestToDraft</c>). This causes rate-limit headroom to be stored under the
    /// <c>resource=graphql</c> tag rather than <c>resource=core</c>.
    /// Default is <c>false</c> (REST API calls consume the <c>core</c> quota).
    /// </param>
    protected async Task<T> ExecuteWithResilienceAsync<T>(
        Func<IGitHubClient, Task<T>> operation, string operationName, CancellationToken ct,
        bool isGraphQL = false)
    {
        var context = ResilienceContextPool.Shared.Get(operationName, ct);
        try
        {
            return await _resiliencePipeline.ExecuteAsync(async ctx =>
            {
                var client = await GetClientAsync(ctx.CancellationToken);
                try
                {
                    var result = await operation(client);

                    // Capture rate-limit info before the transient client is discarded.
                    // GetLastApiInfo() returns null if the client has not made any calls yet
                    // (e.g. when the Polly lambda itself never reached the API call).
                    // TODO: CaptureRateLimitInfo is only called on the success path. On a
                    // RateLimitExceededException / AbuseException attempt — precisely when remaining
                    // is 0 or near-0 and the gauge signal is most valuable — the rate-limit value
                    // is not captured even though GetLastApiInfo() carries it after the failed
                    // response. Consider calling CaptureRateLimitInfo in the rate_limited catch
                    // arms as well to reflect the depleted-quota condition in the gauge.
                    CaptureRateLimitInfo(client, isGraphQL);

                    GitHubTelemetry.ApiRequests.Add(1,
                        new KeyValuePair<string, object?>("operation", operationName),
                        new KeyValuePair<string, object?>("outcome", "success"));
                    return result;
                }
                catch (Octokit.NotFoundException)
                {
                    // Not retried by Polly — record once and re-throw bare.
                    // Do NOT wrap in PipelineRateLimitExceededException.
                    GitHubTelemetry.ApiRequests.Add(1,
                        new KeyValuePair<string, object?>("operation", operationName),
                        new KeyValuePair<string, object?>("outcome", "not_found"));
                    throw;
                }
                catch (Octokit.RateLimitExceededException)
                {
                    // Retried by Polly — emitted once per attempt.
                    GitHubTelemetry.ApiRequests.Add(1,
                        new KeyValuePair<string, object?>("operation", operationName),
                        new KeyValuePair<string, object?>("outcome", "rate_limited"));
                    throw;
                }
                catch (AbuseException)
                {
                    // Retried by Polly — emitted once per attempt.
                    GitHubTelemetry.ApiRequests.Add(1,
                        new KeyValuePair<string, object?>("operation", operationName),
                        new KeyValuePair<string, object?>("outcome", "rate_limited"));
                    throw;
                }
                catch (Exception)
                {
                    // Covers AuthorizationException (retried up to 3 times — emitted per attempt),
                    // transient HttpRequestException, 5xx ApiException, and any other exception.
                    GitHubTelemetry.ApiRequests.Add(1,
                        new KeyValuePair<string, object?>("operation", operationName),
                        new KeyValuePair<string, object?>("outcome", "error"));
                    throw;
                }
            }, context);
        }
        catch (Octokit.AuthorizationException)
        {
            // Authorization failure is propagated to the caller for handling and logging.
            // Logging here would cause duplicate log entries since callers already handle this exception.
            throw;
        }
        catch (Octokit.RateLimitExceededException ex)
        {
            Log.Warning(ex, RateLimitLogMessage, ex.Reset);
            throw new PipelineRateLimitExceededException(ex.Reset, ex);
        }
        catch (AbuseException ex)
        {
            var resetAt = ex.RetryAfterSeconds.HasValue
                ? _timeProvider.GetUtcNow().AddSeconds(ex.RetryAfterSeconds.Value)
                : _timeProvider.GetUtcNow().Add(DefaultRateLimitWait);
            Log.Warning(ex, RateLimitLogMessage, resetAt);
            throw new PipelineRateLimitExceededException(resetAt, ex);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>
    /// Captures rate-limit remaining value from the client's last API response and stores it in
    /// <see cref="GitHubTelemetry"/> for the observable gauge.
    /// Must be called inside the Polly lambda, before the transient client is discarded.
    /// </summary>
    private static void CaptureRateLimitInfo(IGitHubClient client, bool isGraphQL)
    {
        var remaining = client.GetLastApiInfo()?.RateLimit?.Remaining;
        if (remaining.HasValue)
            GitHubTelemetry.UpdateRateLimit(isGraphQL ? "graphql" : "core", remaining.Value);
    }

    /// <summary>
    /// Executes a void-returning Octokit API call with resilience and rate limit handling.
    /// </summary>
    protected async Task ExecuteWithResilienceAsync(
        Func<IGitHubClient, Task> operation, string operationName, CancellationToken ct,
        bool isGraphQL = false)
    {
        await ExecuteWithResilienceAsync(async client =>
        {
            await operation(client);
            return true;
        }, operationName, ct, isGraphQL);
    }

    /// <summary>
    /// Executes an Octokit API call with consistent rate limit exception handling.
    /// Catches <see cref="Octokit.RateLimitExceededException"/> and <see cref="AbuseException"/>,
    /// wrapping them in <see cref="PipelineRateLimitExceededException"/>.
    /// </summary>
    protected async Task<T> ExecuteWithRateLimitHandlingAsync<T>(Func<Task<T>> apiCall)
    {
        try
        {
            return await apiCall();
        }
        catch (Octokit.RateLimitExceededException ex)
        {
            Log.Warning(ex, RateLimitLogMessage, ex.Reset);
            throw new PipelineRateLimitExceededException(ex.Reset, ex);
        }
        catch (AbuseException ex)
        {
            var resetAt = ex.RetryAfterSeconds.HasValue
                ? _timeProvider.GetUtcNow().AddSeconds(ex.RetryAfterSeconds.Value)
                : _timeProvider.GetUtcNow().Add(DefaultRateLimitWait);
            Log.Warning(ex, RateLimitLogMessage, resetAt);
            throw new PipelineRateLimitExceededException(resetAt, ex);
        }
    }

    /// <summary>
    /// Executes a void-returning Octokit API call with consistent rate limit exception handling.
    /// </summary>
    protected async Task ExecuteWithRateLimitHandlingAsync(Func<Task> apiCall)
    {
        try
        {
            await apiCall();
        }
        catch (Octokit.RateLimitExceededException ex)
        {
            Log.Warning(ex, RateLimitLogMessage, ex.Reset);
            throw new PipelineRateLimitExceededException(ex.Reset, ex);
        }
        catch (AbuseException ex)
        {
            var resetAt = ex.RetryAfterSeconds.HasValue
                ? _timeProvider.GetUtcNow().AddSeconds(ex.RetryAfterSeconds.Value)
                : _timeProvider.GetUtcNow().Add(DefaultRateLimitWait);
            Log.Warning(ex, RateLimitLogMessage, resetAt);
            throw new PipelineRateLimitExceededException(resetAt, ex);
        }
    }

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
