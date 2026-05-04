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

    protected GitHubProviderBase(string apiUrl, string token, string owner, string repo)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        _clientProvider = new GitHubClientProvider(apiUrl, token);
        Owner = owner;
        Repo = repo;
        _resiliencePipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(Log.Logger);
    }

    protected GitHubProviderBase(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider, string owner, string repo)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        _clientProvider = new GitHubClientProvider(apiUrl, tokenProvider);
        Owner = owner;
        Repo = repo;
        _resiliencePipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(Log.Logger);
    }

    protected GitHubProviderBase(IGitHubClient client, string owner, string repo)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        _clientProvider = new GitHubClientProvider(client);
        Owner = owner;
        Repo = repo;
        _resiliencePipeline = ResiliencePipelineFactory.CreateGitHubApiPipeline(Log.Logger);
    }

    protected GitHubProviderBase(IGitHubClient client, string token, string owner, string repo)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        _clientProvider = new GitHubClientProvider(client, token);
        Owner = owner;
        Repo = repo;
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
    // TODO: [RES-07] operationName is accepted but not wired to ResilienceContext.OperationKey,
    // so OnRetry logs always show the fallback "GitHubApi" instead of the actual operation name.
    protected async Task<T> ExecuteWithResilienceAsync<T>(
        Func<IGitHubClient, Task<T>> operation, string operationName, CancellationToken ct)
    {
        try
        {
            return await _resiliencePipeline.ExecuteAsync(async token =>
            {
                var client = await GetClientAsync(token);
                return await operation(client);
            }, ct);
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
