using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Pure unit tests for <see cref="QualityGateExecutor.ClassifyRetryOutcome"/>.
/// These tests exercise all discriminating input combinations in isolation,
/// without requiring a full ProceedToQualityGatesAsync round-trip.
/// </summary>
public class QualityGateExecutorRetryLoopClassifierTests
{
    // ── Null agentResult (absorbed exception) ────────────────────────────────

    [Fact]
    public void NullAgentResult_ReturnsTransientWait()
    {
        // null signals that ExecuteAgentAndRecordAsync absorbed a non-cancellation exception.
        // Must be classified as TransientWait so the consecutive counter increments and the cap
        // can fire for exception-surfaced provider failures, not only ErrorCategory-based ones.
        var outcome = QualityGateExecutor.ClassifyRetryOutcome(null);

        outcome.Should().Be(RetryOutcome.TransientWait);
    }

    // ── Provider transient errors → TransientWait ─────────────────────────────

    [Fact]
    public void ProviderRateLimit_ReturnsTransientWait()
    {
        var result = new AgentResult
        {
            ExitCode = 1,
            OutputLines = ["HTTP 429: rate limited"],
            ErrorCategory = AgentErrorCategory.ProviderRateLimit
        };

        var outcome = QualityGateExecutor.ClassifyRetryOutcome(result);

        outcome.Should().Be(RetryOutcome.TransientWait);
    }

    [Fact]
    public void ProviderOverload_ReturnsTransientWait()
    {
        var result = new AgentResult
        {
            ExitCode = 1,
            OutputLines = ["HTTP 503: service unavailable"],
            ErrorCategory = AgentErrorCategory.ProviderOverload
        };

        var outcome = QualityGateExecutor.ClassifyRetryOutcome(result);

        outcome.Should().Be(RetryOutcome.TransientWait);
    }

    // ── Permanent auth failure → AbortAuth ───────────────────────────────────

    [Fact]
    public void PermanentAuthFailure_ReturnsAbortAuth()
    {
        var result = new AgentResult
        {
            ExitCode = 1,
            OutputLines = ["HTTP 401: unauthorized"],
            ErrorCategory = AgentErrorCategory.PermanentAuthFailure
        };

        var outcome = QualityGateExecutor.ClassifyRetryOutcome(result);

        outcome.Should().Be(RetryOutcome.AbortAuth);
    }

    // ── Dead/exhausted session → RestartSession ──────────────────────────────

    // TODO: [WARNING] The RestartSession classifier guard requires all three conditions:
    // ExitCode == 0 AND TotalTokens == 0 AND OutputLines.Count == 0. The tests below only
    // exercise the fully-satisfied case. Missing boundary tests:
    //   - ExitCode == 0, TotalTokens == 0, OutputLines non-empty → should return Retry
    //   - ExitCode != 0, TotalTokens == 0, OutputLines empty    → should return Retry
    // A regression that removes one conjunct from the production classifier would not be
    // caught without these partial-match tests.

    [Fact]
    public void ExitCodeZero_ZeroTokens_EmptyOutput_ReturnsRestartSession()
    {
        // TODO: [WARNING] This test relies on new TokenUsage() producing TotalTokens == 0 because
        // all fields (InputTokens, OutputTokens, ReasoningTokens) default to 0. If TotalTokens is
        // a computed property and any field ever gains a non-zero default, this arrangement silently
        // stops hitting the RestartSession branch and returns Retry instead, making the test
        // vacuously pass on the wrong path. Consider setting fields explicitly:
        //   Usage = new TokenUsage { InputTokens = 0, OutputTokens = 0, ReasoningTokens = 0 }
        // or asserting the precondition: Assert.Equal(0, result.Usage.TotalTokens).
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage() // TotalTokens = InputTokens + OutputTokens + ReasoningTokens = 0
        };

        var outcome = QualityGateExecutor.ClassifyRetryOutcome(result);

        outcome.Should().Be(RetryOutcome.RestartSession);
    }

    // ── Normal successful result → Retry ─────────────────────────────────────

    [Fact]
    public void ExitCodeZero_WithTokensAndOutput_ReturnsRetry()
    {
        var result = new AgentResult
        {
            ExitCode = 0,
            OutputLines = ["Fixed the issue"],
            Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
        };

        var outcome = QualityGateExecutor.ClassifyRetryOutcome(result);

        outcome.Should().Be(RetryOutcome.Retry);
    }

    [Fact]
    public void ExitCodeNonZero_NoCategorySet_ReturnsRetry()
    {
        // A general failure (e.g. agent returned non-zero exit) with no special error category
        // is treated as a normal retry attempt.
        var result = new AgentResult
        {
            ExitCode = 1,
            OutputLines = ["Error: compilation failed"],
            ErrorCategory = AgentErrorCategory.None
        };

        var outcome = QualityGateExecutor.ClassifyRetryOutcome(result);

        outcome.Should().Be(RetryOutcome.Retry);
    }
}
