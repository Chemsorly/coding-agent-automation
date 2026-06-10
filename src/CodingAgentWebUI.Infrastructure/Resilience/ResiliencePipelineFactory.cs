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
    internal static readonly TimeSpan SignalRTimeout = TimeSpan.FromSeconds(30);

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
                    .Handle<ApiException>(ex => IsRetryableApiException(ex))
                    .Handle<Octokit.RateLimitExceededException>()
                    .Handle<AbuseException>(),
                DelayGenerator = args => GetRateLimitDelay(args),
                OnRetry = args =>
                {
                    System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent("retry", tags: new System.Diagnostics.ActivityTagsCollection
                    {
                        { "attempt", args.AttemptNumber + 1 },
                        { "exception_type", args.Outcome.Exception?.GetType().Name ?? "unknown" }
                    }));
                    logger.Warning(
                        "{Operation} retry {Attempt}/{MaxAttempts} after {Exception}",
                        args.Context.OperationKey ?? "GitHubApi",
                        args.AttemptNumber + 1,
                        DefaultMaxRetryAttempts,
                        args.Outcome.Exception?.GetType().Name ?? "unknown");
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
                    .Handle<ApiException>(ex => IsRetryableApiException(ex))
                    .Handle<Octokit.NotFoundException>(ex =>
                        ex.Message.Contains("BlobNotFound", StringComparison.OrdinalIgnoreCase))
                    .Handle<Octokit.RateLimitExceededException>()
                    .Handle<AbuseException>(),
                OnRetry = args =>
                {
                    System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent("retry", tags: new System.Diagnostics.ActivityTagsCollection
                    {
                        { "attempt", args.AttemptNumber + 1 },
                        { "exception_type", args.Outcome.Exception?.GetType().Name ?? "unknown" }
                    }));
                    logger.Warning(
                        "{Operation} retry {Attempt}/{MaxAttempts} after {Exception}",
                        args.Context.OperationKey ?? "GitHubActionsLogs",
                        args.AttemptNumber + 1,
                        DefaultMaxRetryAttempts,
                        args.Outcome.Exception?.GetType().Name ?? "unknown");
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(60))
            .Build();
    }

    /// <summary>
    /// Creates a resilience pipeline for LibGit2Sharp network operations (Clone, Fetch, Push, Pull).
    /// </summary>
    public static ResiliencePipeline CreateGitNetworkPipeline(ILogger logger)
        => CreateGitNetworkPipeline(logger, GitNetworkTimeout);

    internal static ResiliencePipeline CreateGitNetworkPipeline(ILogger logger, TimeSpan timeout)
    {
        return new ResiliencePipelineBuilder()
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
                    System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent("retry", tags: new System.Diagnostics.ActivityTagsCollection
                    {
                        { "attempt", args.AttemptNumber + 1 },
                        { "exception_type", args.Outcome.Exception?.GetType().Name ?? "unknown" }
                    }));
                    logger.Warning(
                        "{Operation} retry {Attempt}/{MaxAttempts} after {Exception}",
                        args.Context.OperationKey ?? "GitNetwork",
                        args.AttemptNumber + 1,
                        2,
                        args.Outcome.Exception?.GetType().Name ?? "unknown");
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(timeout)
            .Build();
    }

    /// <summary>
    /// Creates a resilience pipeline for HTTP client calls (e.g., TokenVendingService).
    /// </summary>
    public static ResiliencePipeline CreateHttpPipeline(ILogger logger)
    {
        return new ResiliencePipelineBuilder()
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
                    System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent("retry", tags: new System.Diagnostics.ActivityTagsCollection
                    {
                        { "attempt", args.AttemptNumber + 1 },
                        { "exception_type", args.Outcome.Exception?.GetType().Name ?? "unknown" }
                    }));
                    logger.Warning(
                        "{Operation} retry {Attempt}/{MaxAttempts} after {Exception}",
                        args.Context.OperationKey ?? "Http",
                        args.AttemptNumber + 1,
                        DefaultMaxRetryAttempts,
                        args.Outcome.Exception?.GetType().Name ?? "unknown");
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(DefaultTimeout)
            .Build();
    }

    /// <summary>
    /// Creates a resilience pipeline for SignalR hub invocations.
    /// </summary>
    public static ResiliencePipeline CreateSignalRPipeline(ILogger logger)
        => CreateSignalRPipeline(logger, SignalRTimeout);

    internal static ResiliencePipeline CreateSignalRPipeline(ILogger logger, TimeSpan timeout)
    {
        return new ResiliencePipelineBuilder()
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
                    System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent("retry", tags: new System.Diagnostics.ActivityTagsCollection
                    {
                        { "attempt", args.AttemptNumber + 1 },
                        { "exception_type", args.Outcome.Exception?.GetType().Name ?? "unknown" }
                    }));
                    logger.Warning(
                        "{Operation} retry {Attempt}/{MaxAttempts} after {Exception}",
                        args.Context.OperationKey ?? "SignalR",
                        args.AttemptNumber + 1,
                        DefaultMaxRetryAttempts,
                        args.Outcome.Exception?.GetType().Name ?? "unknown");
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(timeout)
            .Build();
    }

    /// <summary>
    /// Creates a resilience pipeline for GitLab API (NGitLab) read calls.
    /// Retries on transient errors (5xx, 408, 429, network) with exponential backoff + jitter.
    /// </summary>
    public static ResiliencePipeline CreateGitLabApiPipeline(ILogger logger)
    {
        return new ResiliencePipelineBuilder()
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
                    System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent("retry", tags: new System.Diagnostics.ActivityTagsCollection
                    {
                        { "attempt", args.AttemptNumber + 1 },
                        { "exception_type", args.Outcome.Exception?.GetType().Name ?? "unknown" }
                    }));
                    logger.Warning(
                        "{Operation} retry {Attempt}/{MaxAttempts} after {Exception}",
                        args.Context.OperationKey ?? "GitLabApi",
                        args.AttemptNumber + 1,
                        DefaultMaxRetryAttempts,
                        args.Outcome.Exception?.GetType().Name ?? "unknown");
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(DefaultTimeout)
            .Build();
    }

    /// <summary>
    /// Creates a resilience pipeline for GitLab API write operations (safe-to-retry POST/PUT).
    /// Only retries on 5xx server errors (not 429/408) since write operations may not be idempotent for those.
    /// </summary>
    public static ResiliencePipeline CreateGitLabWritePipeline(ILogger logger)
    {
        const int writeMaxRetryAttempts = 2;

        return new ResiliencePipelineBuilder()
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
                    System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent("retry", tags: new System.Diagnostics.ActivityTagsCollection
                    {
                        { "attempt", args.AttemptNumber + 1 },
                        { "exception_type", args.Outcome.Exception?.GetType().Name ?? "unknown" }
                    }));
                    logger.Warning(
                        "Write operation {Operation} retry {Attempt}/{MaxAttempts}",
                        args.Context.OperationKey ?? "GitLabWrite",
                        args.AttemptNumber + 1,
                        writeMaxRetryAttempts);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(DefaultTimeout)
            .Build();
    }

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
