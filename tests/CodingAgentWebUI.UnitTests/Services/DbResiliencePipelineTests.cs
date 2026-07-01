using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Retry;
using System.Diagnostics;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Verifies the DB resilience pipeline behavior registered by WorkDistributionRegistration.
/// 
/// Key behavior under test (Polly v8 strategy ordering — Issue #943):
/// - CircuitBreaker is outer (first added), Retry is inner (second added)
/// - When circuit is open, requests fail immediately with BrokenCircuitException (no retries)
/// - CB (outer) observes the final outcome of each invocation after retries are exhausted
/// - Circuit trips after MinimumThroughput (5) failed invocations within SamplingDuration
/// </summary>
public class DbResiliencePipelineTests
{
    /// <summary>
    /// Mirrors the production IsTransientDbException predicate from WorkDistributionRegistration.
    /// </summary>
    private static bool IsTransientDbException(Exception ex)
    {
        if (ex is Npgsql.NpgsqlException npgsqlEx && npgsqlEx.IsTransient)
            return true;

        if (ex is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
            return false;

        if (ex is TimeoutException or System.IO.IOException)
            return true;

        return false;
    }

    // TODO: These test helper pipelines mirror production config but are decoupled from the actual
    // WorkDistributionRegistration.RegisterResiliencePipelines code. If production config changes,
    // these tests may still pass with stale values. Consider reading config from the real registration.
    /// <summary>
    /// Builds the "db-request" pipeline with production-equivalent configuration but minimal delays for testing.
    /// Structure: [CircuitBreaker outer] → [Retry(3) inner] → delegate
    /// </summary>
    private static ResiliencePipeline BuildDbRequestPipeline() =>
        new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 1.0,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransientDbException)
            })
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(1), // Fast for tests (production: 500ms)
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = false, // Deterministic for testing
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransientDbException)
            })
            .Build();

    /// <summary>
    /// Builds the "db-background" pipeline with production-equivalent configuration but minimal delays for testing.
    /// Structure: [CircuitBreaker outer] → [Retry(5) inner] → delegate
    /// </summary>
    private static ResiliencePipeline BuildDbBackgroundPipeline() =>
        new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 1.0,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransientDbException)
            })
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 5,
                Delay = TimeSpan.FromMilliseconds(1), // Fast for tests (production: 1s)
                MaxDelay = TimeSpan.FromMilliseconds(10),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = false,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransientDbException)
            })
            .Build();

    // ──────────────────────────────────────────────────────────────────────────────
    // Pipeline resolution from DI
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DbRequestPipeline_ResolvesFromDI()
    {
        using var sp = BuildServiceProvider();
        var provider = sp.GetRequiredService<ResiliencePipelineProvider<string>>();
        var pipeline = provider.GetPipeline("db-request");
        pipeline.Should().NotBeNull();
    }

    [Fact]
    public void DbBackgroundPipeline_ResolvesFromDI()
    {
        using var sp = BuildServiceProvider();
        var provider = sp.GetRequiredService<ResiliencePipelineProvider<string>>();
        var pipeline = provider.GetPipeline("db-background");
        pipeline.Should().NotBeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Circuit breaker trip verification (Issue #943)
    // Proves: CB (outer) observes each invocation's final result after retries exhaust.
    // After 5 failed invocations, the circuit opens and subsequent calls get BrokenCircuitException
    // immediately without entering the retry strategy.
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DbRequestPipeline_CircuitOpensAfter5FailedInvocations_NextCallThrowsBrokenCircuitException()
    {
        var pipeline = BuildDbRequestPipeline();
        var delegateCallCount = 0;

        // Execute 5 invocations that each exhaust retries (1 + 3 = 4 delegate calls each).
        // CB (outer) sees 5 failures → circuit opens.
        for (int i = 0; i < 5; i++)
        {
            try
            {
                await pipeline.ExecuteAsync(async _ =>
                {
                    Interlocked.Increment(ref delegateCallCount);
                    await Task.CompletedTask;
                    throw new TimeoutException("simulated DB timeout");
                });
            }
            catch (TimeoutException)
            {
                // Expected — retries exhausted, CB recorded one failure
            }
            catch (BrokenCircuitException)
            {
                // CB opened during this invocation
            }
        }

        // The circuit should now be open. The next call must throw BrokenCircuitException
        // WITHOUT invoking the delegate (CB rejects at the outer layer before reaching retry).
        var preCallCount = delegateCallCount;

        var act = () => pipeline.ExecuteAsync(async _ =>
        {
            Interlocked.Increment(ref delegateCallCount);
            await Task.CompletedTask;
            throw new TimeoutException("should not reach here");
        }).AsTask();

        await act.Should().ThrowAsync<BrokenCircuitException>();

        // Delegate should NOT have been called (CB rejected immediately at the outer layer)
        delegateCallCount.Should().Be(preCallCount,
            "when circuit is open, the delegate must not be invoked (CB is outer, rejects before retry)");
    }

    [Fact]
    public async Task DbBackgroundPipeline_CircuitOpensAfter5FailedInvocations_NextCallThrowsBrokenCircuitException()
    {
        var pipeline = BuildDbBackgroundPipeline();
        var delegateCallCount = 0;

        // Execute 5 invocations that each exhaust retries (1 + 5 = 6 delegate calls each).
        // CB (outer) sees 5 failures → circuit opens.
        for (int i = 0; i < 5; i++)
        {
            try
            {
                await pipeline.ExecuteAsync(async _ =>
                {
                    Interlocked.Increment(ref delegateCallCount);
                    await Task.CompletedTask;
                    throw new TimeoutException("simulated DB timeout");
                });
            }
            catch (TimeoutException)
            {
                // Expected
            }
            catch (BrokenCircuitException)
            {
                // CB opened during this invocation
            }
        }

        // Circuit should be open now
        var preCallCount = delegateCallCount;

        var act = () => pipeline.ExecuteAsync(async _ =>
        {
            Interlocked.Increment(ref delegateCallCount);
            await Task.CompletedTask;
            throw new TimeoutException("should not reach here");
        }).AsTask();

        await act.Should().ThrowAsync<BrokenCircuitException>();

        delegateCallCount.Should().Be(preCallCount,
            "when circuit is open, the delegate must not be invoked (CB is outer, rejects before retry)");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Fast-fail timing: When circuit is open, BrokenCircuitException is immediate
    // Proves: With CB as outer strategy, open circuit rejects before entering retry,
    // so there is zero retry delay.
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DbRequestPipeline_WhenCircuitOpen_FailsImmediatelyWithoutRetryDelay()
    {
        var pipeline = BuildDbRequestPipeline();

        // Trip the circuit (5 failed invocations)
        await TripCircuit(pipeline, invocations: 5);

        // Measure how fast the next call fails
        var sw = Stopwatch.StartNew();
        var act = () => pipeline.ExecuteAsync(async _ =>
        {
            await Task.CompletedTask;
            throw new TimeoutException("should not reach here");
        }).AsTask();

        await act.Should().ThrowAsync<BrokenCircuitException>();
        sw.Stop();

        // Must be near-instant (CB rejects at outer layer, retry never entered)
        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "CB (outer) should reject immediately without entering retry strategy");
    }

    [Fact]
    public async Task DbBackgroundPipeline_WhenCircuitOpen_FailsImmediatelyWithoutRetryDelay()
    {
        var pipeline = BuildDbBackgroundPipeline();

        // Trip the circuit (5 failed invocations)
        await TripCircuit(pipeline, invocations: 5);

        // Measure how fast the next call fails
        var sw = Stopwatch.StartNew();
        var act = () => pipeline.ExecuteAsync(async _ =>
        {
            await Task.CompletedTask;
            throw new TimeoutException("should not reach here");
        }).AsTask();

        await act.Should().ThrowAsync<BrokenCircuitException>();
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "CB (outer) should reject immediately without entering retry strategy");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // BrokenCircuitException is NOT retried
    // Proves: When circuit is open, the request never reaches the retry strategy.
    // The delegate is never invoked because CB (outer) rejects before retry (inner).
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DbRequestPipeline_BrokenCircuitException_DelegateNeverInvoked()
    {
        var pipeline = BuildDbRequestPipeline();

        // Trip the circuit
        await TripCircuit(pipeline, invocations: 5);

        // Track how many times the delegate is called
        var delegateCallCount = 0;

        var act = () => pipeline.ExecuteAsync(async _ =>
        {
            Interlocked.Increment(ref delegateCallCount);
            await Task.CompletedTask;
            throw new TimeoutException("should not be called");
        }).AsTask();

        await act.Should().ThrowAsync<BrokenCircuitException>();

        // Zero delegate calls — CB (outer) rejects before reaching retry or delegate
        delegateCallCount.Should().Be(0,
            "circuit breaker (outer) should reject before the request reaches retry or delegate");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // CB (outer) sees invocation-level failures, not individual retry attempts
    // Proves: With the corrected ordering, the CB's failure count increments once per
    // invocation (after retries exhaust), not once per retry attempt.
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DbRequestPipeline_CircuitBreaker_SeesOneFailurePerInvocation_NotPerRetryAttempt()
    {
        var pipeline = BuildDbRequestPipeline();
        var delegateCallCount = 0;

        // First invocation: retry makes 1 + 3 = 4 attempts (all fail with TimeoutException).
        // CB (outer) sees ONE failure (the final TimeoutException after retries exhaust).
        try
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                Interlocked.Increment(ref delegateCallCount);
                await Task.CompletedTask;
                throw new TimeoutException("simulated");
            });
        }
        catch (TimeoutException)
        {
            // Expected — all retries exhausted, CB recorded one failure
        }

        // Verify: the delegate was called 4 times (1 original + 3 retries)
        delegateCallCount.Should().Be(4,
            "retry (inner) should make 1 + MaxRetryAttempts=3 attempts through delegate");

        // After 1 invocation, CB has recorded 1 failure. Circuit is NOT yet open (needs 5).
        // Verify the circuit is still closed by making another call that reaches the delegate.
        var secondCallDelegateCount = 0;
        try
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                Interlocked.Increment(ref secondCallDelegateCount);
                await Task.CompletedTask;
                throw new TimeoutException("simulated");
            });
        }
        catch (TimeoutException)
        {
            // Expected — CB still closed, retries exhausted
        }

        // Second invocation also had retries, confirming CB is still closed after 2 invocations
        secondCallDelegateCount.Should().Be(4,
            "circuit should still be closed after only 2 failed invocations (needs 5 to trip)");

        // Complete 3 more invocations to trip the circuit (total = 5)
        for (int i = 0; i < 3; i++)
        {
            try
            {
                await pipeline.ExecuteAsync(async _ =>
                {
                    await Task.CompletedTask;
                    throw new TimeoutException("simulated");
                });
            }
            catch (TimeoutException) { }
            catch (BrokenCircuitException) { }
        }

        // Now verify the circuit IS open
        var act = () => pipeline.ExecuteAsync(async _ =>
        {
            await Task.CompletedTask;
            throw new TimeoutException("should not reach");
        }).AsTask();

        await act.Should().ThrowAsync<BrokenCircuitException>(
            "after 5 failed invocations, circuit must be open");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Trips the circuit by sending enough failing invocations through the pipeline.
    /// Each invocation exhausts retries, and CB (outer) records one failure per invocation.
    /// </summary>
    private static async Task TripCircuit(ResiliencePipeline pipeline, int invocations)
    {
        for (int i = 0; i < invocations; i++)
        {
            try
            {
                await pipeline.ExecuteAsync(async _ =>
                {
                    await Task.CompletedTask;
                    throw new TimeoutException("trip circuit");
                });
            }
            catch (TimeoutException) { }
            catch (BrokenCircuitException) { break; } // Circuit already open
        }
    }

    // TODO: BuildServiceProvider() wraps services.AddWorkDistribution(config) in a try/catch that
    // silently swallows all exceptions. If pipeline registration fails, DI resolution tests will
    // fail with misleading "pipeline not found" instead of the actual root cause.
    /// <summary>
    /// Builds a ServiceProvider with WorkDistribution configured (for DI resolution tests).
    /// </summary>
    private static ServiceProvider BuildServiceProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Host"] = "localhost",
                ["Database:Name"] = "test",
                ["WorkDistribution:Mode"] = "SignalR",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        try
        {
            services.AddWorkDistribution(config);
        }
        catch (Exception)
        {
            // Pipeline registration happens early; other DI failures are acceptable
        }

        return services.BuildServiceProvider();
    }
}
