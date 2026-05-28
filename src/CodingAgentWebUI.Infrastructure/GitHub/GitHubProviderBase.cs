using Octokit;
using Polly;
using Serilog;
using CodingAgentWebUI.Infrastructure.Resilience;
using PipelineRateLimitExceededException = CodingAgentWebUI.Pipeline.Models.RateLimitExceededException;

namespace CodingAgentWebUI.Infrastructure.GitHub;

/// <summary>
/// Base class for GitHub providers that share common authentication,
/// client management, and repository validation patterns.
/// </summary>
public abstract class GitHubProviderBase : IAsyncDisposable
{
    private readonly GitHubClientProvider _clientProvider;
    private readonly ResiliencePipeline _resiliencePipeline;

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
    }

    protected GitHubProviderBase(GitHubConnectionInfo connection, Func<CancellationToken, Task<string>> tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _clientProvider = new GitHubClientProvider(connection.ApiUrl, tokenProvider);
        Owner = connection.Owner;
        Repo = connection.Repo;
        _resiliencePipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(Log.Logger);
    }

    protected GitHubProviderBase(GitHubConnectionInfo connection, IGitHubClient client)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(client);
        _clientProvider = new GitHubClientProvider(client);
        Owner = connection.Owner;
        Repo = connection.Repo;
        _resiliencePipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(Log.Logger);
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
    }

    /// <summary>API base URL from the client provider.</summary>
    protected string? ApiUrl => _clientProvider.ApiUrl;

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
            throw new ArgumentException(
                $"Invalid issue identifier: '{identifier}'. Expected a numeric issue number.",
                nameof(identifier));
        return issueNumber;
    }

    private static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Executes an Octokit API call with resilience (retry on transient errors) and rate limit handling.
    /// Acquires a fresh client inside the retry loop to ensure token freshness on retry.
    /// </summary>
    protected async Task<T> ExecuteWithResilienceAsync<T>(
        Func<IGitHubClient, Task<T>> operation, string operationName, CancellationToken ct)
    {
        var context = ResilienceContextPool.Shared.Get(operationName, ct);
        try
        {
            return await _resiliencePipeline.ExecuteAsync(async ctx =>
            {
                var client = await GetClientAsync(ctx.CancellationToken);
                return await operation(client);
            }, context);
        }
        catch (Octokit.RateLimitExceededException ex)
        {
            throw new PipelineRateLimitExceededException(ex.Reset, ex);
        }
        catch (AbuseException ex)
        {
            var resetAt = ex.RetryAfterSeconds.HasValue
                ? DateTimeOffset.UtcNow.AddSeconds(ex.RetryAfterSeconds.Value)
                : DateTimeOffset.UtcNow.Add(DefaultRateLimitWait);
            throw new PipelineRateLimitExceededException(resetAt, ex);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>
    /// Executes a void-returning Octokit API call with resilience and rate limit handling.
    /// </summary>
    protected async Task ExecuteWithResilienceAsync(
        Func<IGitHubClient, Task> operation, string operationName, CancellationToken ct)
    {
        await ExecuteWithResilienceAsync(async client =>
        {
            await operation(client);
            return true;
        }, operationName, ct);
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
            throw new PipelineRateLimitExceededException(ex.Reset, ex);
        }
        catch (AbuseException ex)
        {
            var resetAt = ex.RetryAfterSeconds.HasValue
                ? DateTimeOffset.UtcNow.AddSeconds(ex.RetryAfterSeconds.Value)
                : DateTimeOffset.UtcNow.Add(DefaultRateLimitWait);
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
            throw new PipelineRateLimitExceededException(ex.Reset, ex);
        }
        catch (AbuseException ex)
        {
            var resetAt = ex.RetryAfterSeconds.HasValue
                ? DateTimeOffset.UtcNow.AddSeconds(ex.RetryAfterSeconds.Value)
                : DateTimeOffset.UtcNow.Add(DefaultRateLimitWait);
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
