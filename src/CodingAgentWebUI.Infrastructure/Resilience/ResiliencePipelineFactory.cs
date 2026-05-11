using System.Net.Sockets;
using LibGit2Sharp;
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

    /// <summary>
    /// Creates a resilience pipeline for GitHub API (Octokit) calls.
    /// Retries on transient errors (5xx, rate limits, network) with exponential backoff + jitter.
    /// </summary>
    public static ResiliencePipeline CreateGitHubApiPipeline(ILogger logger)
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
                    .Handle<ApiException>(ex => IsRetryableApiException(ex))
                    .Handle<Octokit.RateLimitExceededException>()
                    .Handle<AbuseException>(),
                DelayGenerator = args => GetRateLimitDelay(args),
                OnRetry = args =>
                {
                    logger.Warning(
                        "{Operation} retry {Attempt}/{MaxAttempts} after {Exception}",
                        args.Context.OperationKey ?? "GitHubApi",
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
                    logger.Warning(
                        "{Operation} retry {Attempt}/{MaxAttempts} after {Exception}",
                        args.Context.OperationKey ?? "GitNetwork",
                        args.AttemptNumber + 1,
                        2,
                        args.Outcome.Exception?.GetType().Name ?? "unknown");
                    return ValueTask.CompletedTask;
                }
            })
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
                    logger.Warning(
                        "{Operation} retry {Attempt}/{MaxAttempts} after {Exception}",
                        args.Context.OperationKey ?? "SignalR",
                        args.AttemptNumber + 1,
                        DefaultMaxRetryAttempts,
                        args.Outcome.Exception?.GetType().Name ?? "unknown");
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
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
