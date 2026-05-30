using NGitLab;
using NGitLab.Models;
using Polly;
using Polly.Timeout;
using Serilog;
using CodingAgentWebUI.Infrastructure.Resilience;
using PipelineRateLimitExceededException = CodingAgentWebUI.Pipeline.Models.RateLimitExceededException;

namespace CodingAgentWebUI.Infrastructure.GitLab;

/// <summary>
/// Abstract base class for GitLab providers that share common authentication,
/// client management, resilience, and project validation patterns.
/// Mirrors <see cref="GitHub.GitHubProviderBase"/> for consistency.
/// </summary>
public abstract class GitLabProviderBase : IAsyncDisposable
{
    private readonly GitLabClientProvider _clientProvider;
    private readonly ResiliencePipeline _readPipeline;
    private readonly ResiliencePipeline _writePipeline;

    /// <summary>Cached project HTTP clone URL (populated by <see cref="ValidateAsync"/>).</summary>
    private string? _httpUrlToRepo;

    /// <summary>Cached project path with namespace (populated by <see cref="ValidateAsync"/>).</summary>
    private string? _pathWithNamespace;

    /// <summary>Numeric GitLab project identifier used for all API calls.</summary>
    protected int ProjectId { get; }

    /// <summary>GitLab API base URL.</summary>
    protected string ApiUrl { get; }

    /// <summary>
    /// Cached HTTP clone URL for the project (e.g., "https://gitlab.com/group/project.git").
    /// Populated after <see cref="ValidateAsync"/> is called.
    /// </summary>
    protected string? HttpUrlToRepo => _httpUrlToRepo;

    /// <summary>
    /// Cached path with namespace for the project (e.g., "group/project").
    /// Populated after <see cref="ValidateAsync"/> is called.
    /// </summary>
    protected string? PathWithNamespace => _pathWithNamespace;

    // Static initialization of libgit2 ownership validation is handled by
    // RepositoryGitOperations static constructor (triggered on first git operation).

    /// <summary>
    /// Creates a provider with a static access token.
    /// Validates that the token and API URL are non-empty (direct construction validation).
    /// </summary>
    protected GitLabProviderBase(string apiUrl, string accessToken, int projectId)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
            throw new ArgumentException("A valid API URL is required.", nameof(apiUrl));
        ValidateApiUrlScheme(apiUrl);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("A valid access token is required.", nameof(accessToken));

        ApiUrl = apiUrl;
        ProjectId = projectId;
        _clientProvider = new GitLabClientProvider(apiUrl, accessToken);
        _readPipeline = ResiliencePipelineFactory.CreateGitLabApiPipeline(Log.Logger);
        _writePipeline = ResiliencePipelineFactory.CreateGitLabWritePipeline(Log.Logger);
    }

    /// <summary>
    /// Creates a provider with a dynamic token provider delegate (for OrchestratorProxy token refresh).
    /// Validates that the API URL is non-empty.
    /// </summary>
    protected GitLabProviderBase(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider, int projectId)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
            throw new ArgumentException("A valid API URL is required.", nameof(apiUrl));
        ValidateApiUrlScheme(apiUrl);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        ApiUrl = apiUrl;
        ProjectId = projectId;
        _clientProvider = new GitLabClientProvider(apiUrl, tokenProvider);
        _readPipeline = ResiliencePipelineFactory.CreateGitLabApiPipeline(Log.Logger);
        _writePipeline = ResiliencePipelineFactory.CreateGitLabWritePipeline(Log.Logger);
    }

    /// <summary>
    /// Creates a provider with a pre-built <see cref="IGitLabClient"/> for testing.
    /// </summary>
    internal GitLabProviderBase(IGitLabClient client, int projectId)
    {
        ArgumentNullException.ThrowIfNull(client);

        ApiUrl = string.Empty;
        ProjectId = projectId;
        _clientProvider = new GitLabClientProvider(client);
        _readPipeline = ResiliencePipelineFactory.CreateGitLabApiPipeline(Log.Logger);
        _writePipeline = ResiliencePipelineFactory.CreateGitLabWritePipeline(Log.Logger);
    }

    /// <summary>
    /// Returns an <see cref="IGitLabClient"/> configured with a current token.
    /// </summary>
    protected Task<IGitLabClient> GetClientAsync(CancellationToken ct)
        => _clientProvider.GetClientAsync(ct);

    /// <summary>
    /// Returns a current token string for LibGit2Sharp operations (clone, push).
    /// </summary>
    protected Task<string> GetTokenAsync(CancellationToken ct)
        => _clientProvider.GetTokenAsync(ct);

    /// <summary>
    /// Validates the provider configuration by retrieving project metadata from the GitLab API.
    /// Caches <c>http_url_to_repo</c> and <c>path_with_namespace</c> for subsequent operations.
    /// </summary>
    public virtual async Task ValidateAsync(CancellationToken ct)
    {
        try
        {
            var project = await ExecuteWithResilienceAsync(
                async client =>
                {
                    var projectClient = client.Projects;
                    return await Task.Run(() => projectClient[ProjectId], ct);
                },
                "ValidateProject", ct);

            _httpUrlToRepo = project.HttpUrl;
            _pathWithNamespace = project.PathWithNamespace;
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 401 || (int)ex.StatusCode == 403)
        {
            throw new InvalidOperationException(
                $"Authentication or authorization failure for GitLab project {ProjectId}. " +
                $"Verify the access token has sufficient permissions.", ex);
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 404)
        {
            throw new InvalidOperationException(
                $"GitLab project {ProjectId} not found or not accessible. " +
                $"Verify the project ID and access token permissions.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Unable to connect to GitLab API at '{ApiUrl}'. " +
                $"Verify the API URL is correct and the server is reachable.", ex);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new InvalidOperationException(
                $"GitLab API request timed out for project {ProjectId} at '{ApiUrl}'.", ex);
        }
    }

    /// <summary>
    /// Executes an async NGitLab API call with resilience (retry on transient errors) and rate limit handling.
    /// Acquires a fresh client inside the retry loop to ensure token freshness on retry.
    /// </summary>
    protected async Task<T> ExecuteWithResilienceAsync<T>(
        Func<IGitLabClient, Task<T>> operation, string operationName, CancellationToken ct)
    {
        var context = ResilienceContextPool.Shared.Get(operationName, ct);
        try
        {
            return await _readPipeline.ExecuteAsync(async ctx =>
            {
                var client = await GetClientAsync(ctx.CancellationToken);
                return await operation(client);
            }, context);
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 429)
        {
            // NGitLab does not expose Retry-After header — estimate reset time
            var resetAt = DateTimeOffset.UtcNow.AddSeconds(60);
            throw new PipelineRateLimitExceededException(resetAt, ex);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>
    /// Executes a void-returning async NGitLab API call with resilience and rate limit handling.
    /// </summary>
    protected async Task ExecuteWithResilienceAsync(
        Func<IGitLabClient, Task> operation, string operationName, CancellationToken ct)
    {
        await ExecuteWithResilienceAsync(async client =>
        {
            await operation(client);
            return true;
        }, operationName, ct);
    }

    /// <summary>
    /// Executes a synchronous NGitLab API call wrapped in <see cref="Task.Run"/> with resilience.
    /// Used for NGitLab methods that only offer synchronous variants.
    /// The Polly pipeline timeout (30s) enforces the cancellation deadline.
    /// </summary>
    protected async Task<T> ExecuteWithResilienceAsync<T>(
        Func<IGitLabClient, T> operation, string operationName, CancellationToken ct)
    {
        return await ExecuteWithResilienceAsync(
            async client => await Task.Run(() => operation(client), ct),
            operationName, ct);
    }

    /// <summary>
    /// Executes an async NGitLab write operation with resilience (fewer retries, only 5xx retried).
    /// </summary>
    protected async Task<T> ExecuteWriteWithResilienceAsync<T>(
        Func<IGitLabClient, Task<T>> operation, string operationName, CancellationToken ct)
    {
        var context = ResilienceContextPool.Shared.Get(operationName, ct);
        try
        {
            return await _writePipeline.ExecuteAsync(async ctx =>
            {
                var client = await GetClientAsync(ctx.CancellationToken);
                return await operation(client);
            }, context);
        }
        catch (GitLabException ex) when ((int)ex.StatusCode == 429)
        {
            var resetAt = DateTimeOffset.UtcNow.AddSeconds(60);
            throw new PipelineRateLimitExceededException(resetAt, ex);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>
    /// Executes a void-returning async NGitLab write operation with resilience.
    /// </summary>
    protected async Task ExecuteWriteWithResilienceAsync(
        Func<IGitLabClient, Task> operation, string operationName, CancellationToken ct)
    {
        await ExecuteWriteWithResilienceAsync(async client =>
        {
            await operation(client);
            return true;
        }, operationName, ct);
    }

    /// <summary>
    /// Executes a synchronous NGitLab write operation wrapped in <see cref="Task.Run"/> with resilience.
    /// </summary>
    protected async Task<T> ExecuteWriteWithResilienceAsync<T>(
        Func<IGitLabClient, T> operation, string operationName, CancellationToken ct)
    {
        return await ExecuteWriteWithResilienceAsync(
            async client => await Task.Run(() => operation(client), ct),
            operationName, ct);
    }

    /// <summary>
    /// Parses a string issue/MR identifier into a numeric IID.
    /// </summary>
    protected static int ParseIdentifier(string identifier, string entityType = "issue")
    {
        if (!int.TryParse(identifier, out var iid))
            throw new ArgumentException(
                $"Invalid {entityType} identifier: '{identifier}'. Expected a numeric IID.",
                nameof(identifier));
        return iid;
    }

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        await _clientProvider.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Validates that the API URL uses an HTTP or HTTPS scheme.
    /// Rejects file://, ftp://, and other non-HTTP schemes.
    /// </summary>
    private static void ValidateApiUrlScheme(string apiUrl)
    {
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            throw new ArgumentException(
                $"API URL must use https:// or http:// scheme. Got: '{apiUrl[..Math.Min(apiUrl.Length, 200)]}'.",
                nameof(apiUrl));
        }
    }
}
