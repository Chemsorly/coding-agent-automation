using Microsoft.Extensions.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace CodingAgentWebUI;

public static partial class WorkDistributionRegistration
{
    /// <summary>
    /// Registers two Polly resilience pipelines for DB operations:
    /// - "db-request": 3 retries, 500ms exponential+jitter (~3.5s total) — for HTTP API endpoints
    /// - "db-background": 5 retries, 1s→16s exponential (~31s total) — for DispatchService/ReconciliationService
    /// Both include circuit breaker: open after 5 consecutive failures, half-open after 30s.
    /// </summary>
    private static void RegisterResiliencePipelines(IServiceCollection services)
    {
        services.AddResiliencePipeline("db-request", builder =>
        {
            builder
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromMilliseconds(500),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransientDbException)
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 1.0, // open on 5 consecutive failures
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(30),
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransientDbException)
                });
        });

        services.AddResiliencePipeline("db-background", builder =>
        {
            builder
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 5,
                    Delay = TimeSpan.FromSeconds(1),
                    MaxDelay = TimeSpan.FromSeconds(16),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = false,
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransientDbException)
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 1.0,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(30),
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransientDbException)
                });
        });
    }
}
