using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="ProviderBusyRetry"/>: a call that ends with <see cref="AgentErrorCategory.ProviderRateLimit"/>
/// or <see cref="AgentErrorCategory.ProviderOverload"/> is sent again, with no pipeline run and no stall monitor.
///
/// Every test feeds the <c>attempt</c> delegate from a queue of results and, unless it is about the wait itself,
/// passes <see cref="TimeSpan.Zero"/> as the retry delay, so nothing waits on the wall clock.
/// </summary>
public class ProviderBusyRetryTests
{
    private const string OriginalPrompt = "implement the feature";

    // ── Retried results ────────────────────────────────────────────────────────

    [Fact]
    public async Task BusyThenSuccess_ReturnsTheSuccess()
    {
        var busy = Busy(AgentErrorCategory.ProviderRateLimit);
        var success = Success();
        var attempts = new Attempts(busy, success);
        var beforeRetryCalls = new List<(AgentResult Result, int Consecutive, int Total)>();

        var result = await ProviderBusyRetry.ExecuteAsync(
            attempts.RunAsync, FreshRequest(), TimeSpan.Zero,
            (r, consecutive, total) => beforeRetryCalls.Add((r, consecutive, total)),
            CancellationToken.None);

        result.Should().BeSameAs(success);
        attempts.Calls.Select(c => c.Retry).Should().Equal(0, 1);
        beforeRetryCalls.Should().ContainSingle().Which.Should().Be((busy, 1, 1));
    }

    [Theory]
    [InlineData(AgentErrorCategory.ProviderRateLimit)]
    [InlineData(AgentErrorCategory.ProviderOverload)]
    public async Task BothBusyCategories_AreRetried(AgentErrorCategory category)
    {
        var attempts = new Attempts(Busy(category), Success());

        var result = await RunAsync(attempts);

        result.Success.Should().BeTrue();
        attempts.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task MixedBusyCategories_ShareOneRetryBudget()
    {
        var attempts = new Attempts(
            Busy(AgentErrorCategory.ProviderRateLimit),
            Busy(AgentErrorCategory.ProviderOverload),
            Busy(AgentErrorCategory.ProviderRateLimit),
            Success());
        var beforeRetryCalls = new List<(int Consecutive, int Total)>();

        await ProviderBusyRetry.ExecuteAsync(
            attempts.RunAsync, FreshRequest(), TimeSpan.Zero,
            (_, consecutive, total) => beforeRetryCalls.Add((consecutive, total)),
            CancellationToken.None);

        beforeRetryCalls.Should().Equal((1, 1), (2, 2), (3, 3));
    }

    [Fact]
    public async Task NonBusyFailure_IsNotRetried()
    {
        var failure = new AgentResult { ExitCode = 1, OutputLines = ["compile error"], ErrorCategory = AgentErrorCategory.None };
        var attempts = new Attempts(failure);
        var beforeRetryCalls = 0;

        var result = await ProviderBusyRetry.ExecuteAsync(
            attempts.RunAsync, FreshRequest(), TimeSpan.Zero, (_, _, _) => beforeRetryCalls++, CancellationToken.None);

        result.Should().BeSameAs(failure);
        attempts.Calls.Should().ContainSingle();
        beforeRetryCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(AgentErrorCategory.ProviderQuotaExhausted)]
    [InlineData(AgentErrorCategory.PermanentAuthFailure)]
    [InlineData(AgentErrorCategory.ProviderMisconfigured)]
    [InlineData(AgentErrorCategory.None)]
    public async Task OtherFailures_AreNotRetried(AgentErrorCategory category)
    {
        var failure = new AgentResult { ExitCode = 1, OutputLines = [], ErrorCategory = category };
        var attempts = new Attempts(failure);

        var result = await RunAsync(attempts);

        result.Should().BeSameAs(failure);
        attempts.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task SuccessfulResult_IsNotRetried_EvenWithABusyCategory()
    {
        var success = new AgentResult { ExitCode = 0, OutputLines = [], ErrorCategory = AgentErrorCategory.ProviderRateLimit };
        var attempts = new Attempts(success);

        var result = await RunAsync(attempts);

        result.Should().BeSameAs(success);
        attempts.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Retry_WithoutABeforeRetryCallback_StillRetries()
    {
        var attempts = new Attempts(Busy(AgentErrorCategory.ProviderRateLimit), Success());

        var result = await ProviderBusyRetry.ExecuteAsync(
            attempts.RunAsync, FreshRequest(), TimeSpan.Zero, beforeRetry: null, CancellationToken.None);

        result.Success.Should().BeTrue();
        attempts.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ThrownException_IsNotRetried()
    {
        var calls = 0;

        var act = () => ProviderBusyRetry.ExecuteAsync(
            (_, _, _) =>
            {
                calls++;
                throw new InvalidOperationException("provider crashed");
            },
            FreshRequest(), TimeSpan.Zero, beforeRetry: null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("provider crashed");
        calls.Should().Be(1);
    }

    // ── The limits ─────────────────────────────────────────────────────────────

    [Fact]
    public void Limits_AreTenInARowWithoutProgressAndThirtyPerCall()
    {
        ProviderBusyRetry.MaxConsecutiveRetries.Should().Be(10);
        ProviderBusyRetry.MaxRetriesPerCall.Should().Be(30);
    }

    [Fact]
    public async Task ProviderStillBusy_AfterTheConsecutiveLimit_ReturnsTheLastResult()
    {
        var lastResult = Busy(AgentErrorCategory.ProviderRateLimit);
        var attempts = Attempts.Repeating(lastResult);
        var beforeRetryCalls = new List<(int Consecutive, int Total)>();

        var result = await ProviderBusyRetry.ExecuteAsync(
            attempts.RunAsync, FreshRequest(), TimeSpan.Zero,
            (_, consecutive, total) => beforeRetryCalls.Add((consecutive, total)),
            CancellationToken.None);

        result.Should().BeSameAs(lastResult);
        attempts.Calls.Should().HaveCount(ProviderBusyRetry.MaxConsecutiveRetries + 1);
        beforeRetryCalls.Should().HaveCount(ProviderBusyRetry.MaxConsecutiveRetries);
        beforeRetryCalls.Last().Should().Be((ProviderBusyRetry.MaxConsecutiveRetries, ProviderBusyRetry.MaxConsecutiveRetries));
    }

    [Fact]
    public async Task ProgressBetweenBursts_ResetsTheConsecutiveCount_ButNotTheTotal()
    {
        var results = Enumerable.Repeat(Busy(AgentErrorCategory.ProviderRateLimit), ProviderBusyRetry.MaxConsecutiveRetries - 1)
            .Append(BusyAfterProgress(AgentErrorCategory.ProviderRateLimit))
            .Concat(Enumerable.Repeat(Busy(AgentErrorCategory.ProviderRateLimit), ProviderBusyRetry.MaxConsecutiveRetries - 1))
            .Append(Success())
            .ToArray();
        var attempts = new Attempts(results);
        var beforeRetryCalls = new List<(int Consecutive, int Total)>();

        var result = await ProviderBusyRetry.ExecuteAsync(
            attempts.RunAsync, FreshRequest(), TimeSpan.Zero,
            (_, consecutive, total) => beforeRetryCalls.Add((consecutive, total)),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        attempts.Calls.Should().HaveCount(results.Length);
        // The attempt that made progress is the 10th busy result: its retry is again the first of a new burst.
        beforeRetryCalls[ProviderBusyRetry.MaxConsecutiveRetries - 2].Should().Be((ProviderBusyRetry.MaxConsecutiveRetries - 1, ProviderBusyRetry.MaxConsecutiveRetries - 1));
        beforeRetryCalls[ProviderBusyRetry.MaxConsecutiveRetries - 1].Should().Be((1, ProviderBusyRetry.MaxConsecutiveRetries));
        beforeRetryCalls.Last().Total.Should().Be(results.Length - 1);
    }

    [Fact]
    public async Task EveryAttemptMakingProgress_StopsAtThePerCallLimit()
    {
        var lastResult = BusyAfterProgress(AgentErrorCategory.ProviderOverload);
        var attempts = Attempts.Repeating(lastResult);

        var result = await RunAsync(attempts);

        result.Should().BeSameAs(lastResult);
        attempts.Calls.Should().HaveCount(ProviderBusyRetry.MaxRetriesPerCall + 1);
        attempts.Calls.Select(c => c.Retry).Should().Equal(Enumerable.Range(0, ProviderBusyRetry.MaxRetriesPerCall + 1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(500, 0)]
    public async Task BusyAttemptWithoutOutputTokens_IsNotProgress(long inputTokens, long outputTokens)
    {
        var lastResult = new AgentResult
        {
            ExitCode = 1,
            OutputLines = [],
            ErrorCategory = AgentErrorCategory.ProviderRateLimit,
            Usage = new TokenUsage { InputTokens = inputTokens, OutputTokens = outputTokens }
        };
        var attempts = Attempts.Repeating(lastResult);

        await RunAsync(attempts);

        attempts.Calls.Should().HaveCount(ProviderBusyRetry.MaxConsecutiveRetries + 1);
    }

    // ── The retried request ────────────────────────────────────────────────────

    [Fact]
    public async Task ResumedRequest_GetsTheRetryNoteOnce()
    {
        var original = new AgentRequest { Prompt = OriginalPrompt, WorkspacePath = "/ws", UseResume = true };
        var attempts = new Attempts(
            Busy(AgentErrorCategory.ProviderRateLimit), Busy(AgentErrorCategory.ProviderRateLimit), Success());

        await RunAsync(attempts, original);

        var prompts = attempts.Calls.Select(c => c.Request.Prompt).ToList();
        prompts.Should().HaveCount(3);
        prompts[0].Should().Be(OriginalPrompt);
        prompts[1].Should().Be(ProviderBusyRetry.RetryNote + OriginalPrompt);
        prompts[2].Should().Be(ProviderBusyRetry.RetryNote + OriginalPrompt);
        original.Prompt.Should().Be(OriginalPrompt, "the caller's request is never changed");
    }

    [Fact]
    public async Task RequestWithAResumeSessionId_GetsTheRetryNote()
    {
        var original = new AgentRequest { Prompt = OriginalPrompt, WorkspacePath = "/ws", UseResume = false, ResumeSessionId = "session-1" };
        var attempts = new Attempts(Busy(AgentErrorCategory.ProviderOverload), Success());

        await RunAsync(attempts, original);

        attempts.Calls.Select(c => c.Request.Prompt).Should().Equal(OriginalPrompt, ProviderBusyRetry.RetryNote + OriginalPrompt);
    }

    [Fact]
    public async Task FreshRequest_IsSentAgainWithTheSamePrompt()
    {
        var attempts = new Attempts(Busy(AgentErrorCategory.ProviderRateLimit), Success());

        await RunAsync(attempts, FreshRequest());

        attempts.Calls.Select(c => c.Request.Prompt).Should().Equal(OriginalPrompt, OriginalPrompt);
    }

    [Fact]
    public void BuildRetryRequest_CopiesEveryOtherPropertyOfTheOriginal()
    {
        var original = new AgentRequest
        {
            Prompt = OriginalPrompt,
            WorkspacePath = "/ws-copy",
            Timeout = TimeSpan.FromMinutes(7),
            UseResume = true,
            ResumeSessionId = "session-9",
            ImagePaths = ["/images/a.png", "/images/b.png"],
            EnvironmentVariables = new Dictionary<string, string> { ["TOKEN_NAME"] = "value" },
            EffortOverride = AgentEffortLevel.High
        };

        var retried = ProviderBusyRetry.BuildRetryRequest(original);

        retried.Should().NotBeSameAs(original);
        retried.WorkspacePath.Should().Be("/ws-copy");
        retried.Timeout.Should().Be(TimeSpan.FromMinutes(7));
        retried.UseResume.Should().BeTrue();
        retried.ResumeSessionId.Should().Be("session-9");
        retried.ImagePaths!.Should().Equal("/images/a.png", "/images/b.png");
        retried.EnvironmentVariables!.Should().BeEquivalentTo(new Dictionary<string, string> { ["TOKEN_NAME"] = "value" });
        retried.EffortOverride.Should().Be(AgentEffortLevel.High);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildRetryRequest_KeepsTheLightTierFlag(bool useLightTier)
    {
        var original = new AgentRequest { Prompt = OriginalPrompt, WorkspacePath = "/ws", UseLightTier = useLightTier };

        var retried = ProviderBusyRetry.BuildRetryRequest(original);

        retried.UseLightTier.Should().Be(useLightTier);
        retried.AppliesLightTier.Should().Be(useLightTier);
    }

    // ── The wait ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retry_WaitsOnTheInjectedTimeProvider_AfterTheBeforeRetryCallback()
    {
        var fakeTime = new FakeTimeProvider();
        var attempts = new Attempts(Busy(AgentErrorCategory.ProviderRateLimit), Success());
        var beforeRetryCalls = 0;
        // Longer than the safety bound below: a wait on the wall clock could not finish in time.
        var delay = TimeSpan.FromMinutes(5);

        var call = ProviderBusyRetry.ExecuteAsync(
            attempts.RunAsync, FreshRequest(), delay, (_, _, _) => beforeRetryCalls++, CancellationToken.None, fakeTime);

        call.IsCompleted.Should().BeFalse("the call waits for the delay before it sends the request again");
        beforeRetryCalls.Should().Be(1, "the callback runs before the delay");
        attempts.Calls.Should().ContainSingle();

        fakeTime.Advance(delay);
        var result = await call.WaitAsync(TimeSpan.FromSeconds(30));

        result.Success.Should().BeTrue();
        attempts.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task CancelDuringTheWait_Throws()
    {
        var fakeTime = new FakeTimeProvider();
        var attempts = new Attempts(Busy(AgentErrorCategory.ProviderRateLimit), Success());
        using var cts = new CancellationTokenSource();

        var call = ProviderBusyRetry.ExecuteAsync(
            attempts.RunAsync, FreshRequest(), TimeSpan.FromMinutes(5), beforeRetry: null, cts.Token, fakeTime);
        call.IsCompleted.Should().BeFalse();
        await cts.CancelAsync();

        var act = async () => await call;
        await act.Should().ThrowAsync<OperationCanceledException>();
        attempts.Calls.Should().ContainSingle("a cancelled wait sends nothing again");
    }

    [Fact]
    public async Task TheCancellationToken_IsPassedToEveryAttempt()
    {
        using var cts = new CancellationTokenSource();
        var attempts = new Attempts(Busy(AgentErrorCategory.ProviderRateLimit), Success());

        await ProviderBusyRetry.ExecuteAsync(
            attempts.RunAsync, FreshRequest(), TimeSpan.Zero, beforeRetry: null, cts.Token);

        attempts.Calls.Should().OnlyContain(c => c.Token == cts.Token);
    }

    // ── Arguments ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task NullRequest_ThrowsAtTheCall_BeforeAnyAttempt()
    {
        var attempts = new Attempts(Success());

        var act = async () => await ProviderBusyRetry.ExecuteAsync(
            attempts.RunAsync, null!, TimeSpan.Zero, beforeRetry: null, CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("request");
        attempts.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task NullAttempt_ThrowsAtTheCall()
    {
        var act = async () => await ProviderBusyRetry.ExecuteAsync(
            null!, FreshRequest(), TimeSpan.Zero, beforeRetry: null, CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("attempt");
    }

    // ── IsProviderBusy ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AgentErrorCategory.ProviderRateLimit, 1, true)]
    [InlineData(AgentErrorCategory.ProviderOverload, 1, true)]
    [InlineData(AgentErrorCategory.ProviderRateLimit, 0, false)]
    [InlineData(AgentErrorCategory.ProviderQuotaExhausted, 1, false)]
    [InlineData(AgentErrorCategory.PermanentAuthFailure, 1, false)]
    [InlineData(AgentErrorCategory.ProviderMisconfigured, 1, false)]
    [InlineData(AgentErrorCategory.None, 1, false)]
    public void IsProviderBusy_IsTrueOnlyForAFailedRateLimitOrOverload(AgentErrorCategory category, int exitCode, bool expected)
    {
        var result = new AgentResult { ExitCode = exitCode, OutputLines = [], ErrorCategory = category };

        ProviderBusyRetry.IsProviderBusy(result).Should().Be(expected);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static AgentResult Busy(AgentErrorCategory category) =>
        new() { ExitCode = 1, OutputLines = ["provider is busy"], ErrorCategory = category };

    /// <summary>A busy result of an attempt that did work before the provider turned it down: it produced output tokens.</summary>
    private static AgentResult BusyAfterProgress(AgentErrorCategory category) =>
        new()
        {
            ExitCode = 1,
            OutputLines = ["provider is busy"],
            ErrorCategory = category,
            Usage = new TokenUsage { OutputTokens = 100 }
        };

    private static AgentResult Success() => new() { ExitCode = 0, OutputLines = [] };

    private static AgentRequest FreshRequest() => new() { Prompt = OriginalPrompt, WorkspacePath = "/ws" };

    private static Task<AgentResult> RunAsync(Attempts attempts, AgentRequest? request = null) =>
        ProviderBusyRetry.ExecuteAsync(
            attempts.RunAsync, request ?? FreshRequest(), TimeSpan.Zero, beforeRetry: null, CancellationToken.None);

    /// <summary>
    /// The <c>attempt</c> delegate: returns the queued results in order, then <c>repeating</c> when the queue is
    /// empty, and records the request, retry number and token of every call.
    /// </summary>
    private sealed class Attempts
    {
        private readonly Queue<AgentResult> _results;
        private readonly AgentResult? _repeating;

        public Attempts(params AgentResult[] results) => _results = new Queue<AgentResult>(results);

        private Attempts(Queue<AgentResult> results, AgentResult repeating)
        {
            _results = results;
            _repeating = repeating;
        }

        /// <summary>Returns <paramref name="result"/> for every call.</summary>
        public static Attempts Repeating(AgentResult result) => new(new Queue<AgentResult>(), result);

        public List<(AgentRequest Request, int Retry, CancellationToken Token)> Calls { get; } = [];

        public Task<AgentResult> RunAsync(AgentRequest request, int retry, CancellationToken ct)
        {
            Calls.Add((request, retry, ct));
            var result = _results.Count > 0
                ? _results.Dequeue()
                : _repeating ?? throw new InvalidOperationException("The attempt was called more often than results were queued.");
            return Task.FromResult(result);
        }
    }
}
