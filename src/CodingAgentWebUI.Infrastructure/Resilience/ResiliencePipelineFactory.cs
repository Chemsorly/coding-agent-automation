using System.Net.Sockets;
using LibGit2Sharp;
using NGitLab;
using Octokit;
using Polly;
using Polly.Retry;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Infrastructure.Resilience;

/// <summary>
/// Factory for creating Polly v8 resilience pipelines for external network calls.
/// Provides category-specific pipelines for GitHub API, LibGit2Sharp, HTTP, and SignalR.
/// </summary>
public static class ResiliencePipelineFactory
{
    private const int DefaultMaxRetryAttempts = 3;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultOuterTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan GitNetworkTimeout = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan GitNetworkOuterTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan SignalRTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan SignalROuterTimeout = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan HttpOuterTimeout = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan GitLabOuterTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Creates a resilience pipeline for GitHub API (Octokit) calls.
    /// Uses an outer timeout (default: 5 minutes) that wraps the entire retry sequence including
    /// rate-limit delays, and a per-attempt timeout (default: 30s) for individual API calls.
    /// Pattern: outer timeout → retry (with rate-limit-aware backoff) → per-attempt timeout.
    /// </summary>
    public static ResiliencePipeline CreateGitHubApiPipeline(ILogger logger)
        => CreateGitHubApiPipeline(logger, DefaultOuterTimeout, DefaultTimeout);

    internal static ResiliencePipeline CreateGitHubApiPipeline(
        ILogger logger,
        TimeSpan? outerTimeout = null,
        TimeSpan? perAttemptTimeout = null)
    {
        var outer = outerTimeout ?? DefaultOuterTimeout;
        var perAttempt = perAttemptTimeout ?? DefaultTimeout;

        return new ResiliencePipelineBuilder()
            // Outer timeout: caps total time including all retries and rate-limit delays.
            // If a rate-limit reset exceeds this, the operation fails with TimeoutRejectedException.
            .AddTimeout(outer)
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = DefaultMaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<SocketException>()
                    .Handle<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
                    .Handle<Octokit.AuthorizationException>()
                    .Handle<ApiException>(ex => IsRetryableApiException(ex))
                    .Handle<Octokit.RateLimitExceededException>()
                    .Handle<AbuseException>(),
                DelayGenerator = args => GetRateLimitDelay(args),
                OnRetry = args =>
                {
                    RecordRetryEvent(args, logger, "GitHubApi", DefaultMaxRetryAttempts);
                    return ValueTask.CompletedTask;
                }
            })
            // Per-attempt timeout: caps each individual API call (existing 30s behavior).
            .AddTimeout(perAttempt)
            .Build();
    }

    /// <summary>
    /// Creates a resilience pipeline for GitHub Actions log fetching where 404 (BlobNotFound) is retryable.
    /// </summary>
    public static ResiliencePipeline CreateGitHubActionsLogsPipeline(ILogger logger)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = DefaultMaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(5),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<SocketException>()
                    .Handle<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
                    .Handle<Octokit.AuthorizationException>()
                    .Handle<ApiException>(ex => IsRetryableApiException(ex))
                    .Handle<Octokit.NotFoundException>(ex =>
                        ex.Message.Contains("BlobNotFound", StringComparison.OrdinalIgnoreCase))
                    .Handle<Octokit.RateLimitExceededException>()
                    .Handle<AbuseException>(),
                OnRetry = args =>
                {
                    RecordRetryEvent(args, logger, "GitHubActionsLogs", DefaultMaxRetryAttempts);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(60))
            .Build();
    }

    /// <summary>
    /// Creates a resilience pipeline for LibGit2Sharp network operations (Clone, Fetch, Push, Pull).
    /// Uses an outer timeout (default: 5 minutes) that caps the entire retry sequence,
    /// and a per-attempt timeout (default: 120s) for individual git operations.
    /// Pattern: outer timeout → retry → per-attempt timeout.
    /// </summary>
    public static ResiliencePipeline CreateGitNetworkPipeline(ILogger logger)
        => CreateGitNetworkPipeline(logger, GitNetworkTimeout, GitNetworkOuterTimeout);

    internal static ResiliencePipeline CreateGitNetworkPipeline(ILogger logger, TimeSpan timeout, TimeSpan? outerTimeout = null)
    {
        var outer = outerTimeout ?? GitNetworkOuterTimeout;

        return new ResiliencePipelineBuilder()
            // Outer timeout: caps total time including all retries.
            .AddTimeout(outer)
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(2),
                ShouldHandle = new PredicateBuilder()
                    .Handle<LibGit2SharpException>(ex => IsTransientGitException(ex)),
                OnRetry = args =>
                {
                    RecordRetryEvent(args, logger, "GitNetwork", 2);
                    return ValueTask.CompletedTask;
                }
            })
            // Per-attempt timeout: caps each individual git operation.
            .AddTimeout(timeout)
            .Build();
    }

    /// <summary>
    /// Creates a resilience pipeline for HTTP client calls (e.g., TokenVendingService).
    /// Uses an outer timeout (default: 3 minutes) that caps the entire retry sequence,
    /// and a per-attempt timeout (default: 30s) for individual HTTP calls.
    /// Pattern: outer timeout → retry → per-attempt timeout.
    /// </summary>
    public static ResiliencePipeline CreateHttpPipeline(ILogger logger)
    {
        return new ResiliencePipelineBuilder()
            // Outer timeout: caps total time including all retries.
            .AddTimeout(HttpOuterTimeout)
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = DefaultMaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<SocketException>()
                    .Handle<TaskCanceledException>(ex => ex.InnerException is TimeoutException),
                OnRetry = args =>
                {
                    RecordRetryEvent(args, logger, "Http", DefaultMaxRetryAttempts);
                    return ValueTask.CompletedTask;
                }
            })
            // Per-attempt timeout: caps each individual HTTP call.
            .AddTimeout(DefaultTimeout)
            .Build();
    }

    /// <summary>
    /// Creates a resilience pipeline for SignalR hub invocations.
    /// Uses an outer timeout (default: 2 minutes) that caps the entire retry sequence,
    /// and a per-attempt timeout (default: 30s) for individual invocations.
    /// Pattern: outer timeout → retry → per-attempt timeout.
    /// </summary>
    public static ResiliencePipeline CreateSignalRPipeline(ILogger logger)
        => CreateSignalRPipeline(logger, SignalRTimeout, SignalROuterTimeout);

    internal static ResiliencePipeline CreateSignalRPipeline(ILogger logger, TimeSpan timeout, TimeSpan? outerTimeout = null)
    {
        var outer = outerTimeout ?? SignalROuterTimeout;

        return new ResiliencePipelineBuilder()
            // Outer timeout: caps total time including all retries.
            .AddTimeout(outer)
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = DefaultMaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(500),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<SocketException>()
                    .Handle<IOException>()
                    .Handle<InvalidOperationException>(ex =>
                        ex.Message.Contains("not in the 'Connected' state", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("connection was stopped", StringComparison.OrdinalIgnoreCase)),
                OnRetry = args =>
                {
                    RecordRetryEvent(args, logger, "SignalR", DefaultMaxRetryAttempts);
                    return ValueTask.CompletedTask;
                }
            })
            // Per-attempt timeout: caps each individual SignalR invocation.
            .AddTimeout(timeout)
            .Build();
    }

    /// <summary>
    /// Creates a resilience pipeline for GitLab API (NGitLab) read calls.
    /// Retries on transient errors (5xx, 408, 429, network) with exponential backoff + jitter.
    /// Uses an outer timeout (default: 3 minutes) that caps the entire retry sequence,
    /// and a per-attempt timeout (default: 30s) for individual API calls.
    /// Pattern: outer timeout → retry → per-attempt timeout.
    /// </summary>
    public static ResiliencePipeline CreateGitLabApiPipeline(ILogger logger)
    {
        return new ResiliencePipelineBuilder()
            // Outer timeout: caps total time including all retries.
            .AddTimeout(GitLabOuterTimeout)
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = DefaultMaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<SocketException>()
                    .Handle<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
                    .Handle<GitLabException>(ex => IsRetryableGitLabException(ex)),
                OnRetry = args =>
                {
                    RecordRetryEvent(args, logger, "GitLabApi", DefaultMaxRetryAttempts);
                    return ValueTask.CompletedTask;
                }
            })
            // Per-attempt timeout: caps each individual GitLab API call.
            .AddTimeout(DefaultTimeout)
            .Build();
    }

    /// <summary>
    /// Creates a resilience pipeline for GitLab API write operations (safe-to-retry POST/PUT).
    /// Only retries on 5xx server errors (not 429/408) since write operations may not be idempotent for those.
    /// Uses an outer timeout (default: 3 minutes) that caps the entire retry sequence,
    /// and a per-attempt timeout (default: 30s) for individual API calls.
    /// Pattern: outer timeout → retry → per-attempt timeout.
    /// </summary>
    public static ResiliencePipeline CreateGitLabWritePipeline(ILogger logger)
    {
        const int writeMaxRetryAttempts = 2;

        return new ResiliencePipelineBuilder()
            // Outer timeout: caps total time including all retries.
            .AddTimeout(GitLabOuterTimeout)
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = writeMaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<SocketException>()
                    .Handle<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
                    .Handle<GitLabException>(ex => (int)ex.StatusCode >= 500),
                OnRetry = args =>
                {
                    RecordRetryEvent(args, logger, "GitLabWrite", writeMaxRetryAttempts);
                    return ValueTask.CompletedTask;
                }
            })
            // Per-attempt timeout: caps each individual GitLab write call.
            .AddTimeout(DefaultTimeout)
            .Build();
    }

    /// <summary>
    /// Records a retry event on the current Activity span and logs the retry attempt.
    /// </summary>
    private static void RecordRetryEvent(OnRetryArguments<object> args, ILogger logger, string defaultOperationKey, int maxAttempts)
    {
        var ex = args.Outcome.Exception;
        var exType = ex?.GetType().Name ?? "unknown";
        var exMessage = TruncateMessage(ex?.Message);

        System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent("retry",
            tags: new System.Diagnostics.ActivityTagsCollection
            {
                { "attempt", args.AttemptNumber + 1 },
                { "exception.type", exType },
                { "exception.message", exMessage }
            }));

        logger.Warning(
            "{Operation} retry {Attempt}/{MaxAttempts} after {ExceptionType}: {ExceptionMessage}",
            args.Context.OperationKey ?? defaultOperationKey,
            args.AttemptNumber + 1,
            maxAttempts,
            exType,
            exMessage);
    }

    /// <summary>
    /// Truncates a message to the specified maximum length to prevent log explosion.
    /// </summary>
    internal static string TruncateMessage(string? message, int maxLength = 200)
        => message is null ? "unknown"
           : message.Length <= maxLength ? message
           : string.Concat(message.AsSpan(0, maxLength), "…");

    /// <summary>
    /// Determines if a <see cref="GitLabException"/> is retryable (5xx, 408 Request Timeout, 429 Too Many Requests).
    /// </summary>
    internal static bool IsRetryableGitLabException(GitLabException ex)
    {
        var code = (int)ex.StatusCode;
        return code >= 500 || code == 408 || code == 429;
    }

    /// <summary>
    /// Determines if an <see cref="ApiException"/> is retryable (5xx server errors).
    /// </summary>
    internal static bool IsRetryableApiException(ApiException ex)
    {
        var statusCode = (int)ex.StatusCode;
        return statusCode >= 500 && statusCode < 600;
    }

    /// <summary>
    /// Determines if a <see cref="LibGit2SharpException"/> is a transient network error.
    /// Non-retryable: branch protection, conflict, auth failures.
    /// </summary>
    internal static bool IsTransientGitException(LibGit2SharpException ex)
    {
        var message = ex.Message;

        // Non-retryable patterns
        if (message.Contains("protected branch", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("required status check", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("credentials", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("403", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Retryable: known network patterns OR unclassified errors (potentially transient)
        return true;
    }

    /// <summary>
    /// Extracts rate limit delay from exception headers when available.
    /// The delay may exceed the outer timeout — in that case, the outer timeout strategy
    /// will cancel the wait with TimeoutRejectedException, which is the correct fail-fast behaviour.
    /// </summary>
    private static ValueTask<TimeSpan?> GetRateLimitDelay(RetryDelayGeneratorArguments<object> args)
    {
        if (args.Outcome.Exception is Octokit.RateLimitExceededException rateLimitEx)
        {
            var delay = rateLimitEx.Reset - DateTimeOffset.UtcNow;
            // Always honour the rate-limit reset delay. If it exceeds the outer pipeline timeout,
            // the timeout strategy will cancel the wait — which is the correct fail-fast behaviour.
            if (delay > TimeSpan.Zero)
                return new ValueTask<TimeSpan?>(delay);
        }

        if (args.Outcome.Exception is AbuseException abuseEx && abuseEx.RetryAfterSeconds.HasValue)
        {
            return new ValueTask<TimeSpan?>(TimeSpan.FromSeconds(abuseEx.RetryAfterSeconds.Value));
        }

        // Use default exponential backoff
        return new ValueTask<TimeSpan?>((TimeSpan?)null);
    }
}
