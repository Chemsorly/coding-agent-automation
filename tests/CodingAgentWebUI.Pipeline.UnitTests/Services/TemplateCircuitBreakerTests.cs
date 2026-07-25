using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="TemplateCircuitBreaker"/>.
/// </summary>
public class TemplateCircuitBreakerTests
{
    [Fact]
    public void Evaluate_AllTemplatesAtThreshold_ReturnsTrue()
    {
        var cb = new TemplateCircuitBreaker();
        var failures = new Dictionary<string, int>
        {
            ["t1"] = 3,
            ["t2"] = 5,
            ["t3"] = 3
        };

        cb.Evaluate(failures, threshold: 3).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_SomeTemplatesBelowThreshold_ReturnsFalse()
    {
        var cb = new TemplateCircuitBreaker();
        var failures = new Dictionary<string, int>
        {
            ["t1"] = 3,
            ["t2"] = 2,
            ["t3"] = 5
        };

        cb.Evaluate(failures, threshold: 3).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_EmptyDictionary_ReturnsFalse()
    {
        var cb = new TemplateCircuitBreaker();
        var failures = new Dictionary<string, int>();

        cb.Evaluate(failures, threshold: 3).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_AlreadyTripped_ReturnsFalse()
    {
        var cb = new TemplateCircuitBreaker();
        cb.Trip();

        var failures = new Dictionary<string, int>
        {
            ["t1"] = 10,
            ["t2"] = 10
        };

        cb.Evaluate(failures, threshold: 3).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_ExactlyAtThreshold_ReturnsTrue()
    {
        var cb = new TemplateCircuitBreaker();
        var failures = new Dictionary<string, int>
        {
            ["t1"] = 5,
            ["t2"] = 5
        };

        cb.Evaluate(failures, threshold: 5).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_OneBelowThreshold_ReturnsFalse()
    {
        var cb = new TemplateCircuitBreaker();
        var failures = new Dictionary<string, int>
        {
            ["t1"] = 5,
            ["t2"] = 4
        };

        cb.Evaluate(failures, threshold: 5).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_CalledTwiceWithoutTrip_ReturnsTrueBothTimes()
    {
        var cb = new TemplateCircuitBreaker();
        var failures = new Dictionary<string, int>
        {
            ["t1"] = 3,
            ["t2"] = 3
        };

        cb.Evaluate(failures, threshold: 3).Should().BeTrue();
        cb.Evaluate(failures, threshold: 3).Should().BeTrue();
    }

    [Fact]
    public void Trip_SetsIsTrippedAndTimestamp()
    {
        var cb = new TemplateCircuitBreaker();
        var before = DateTimeOffset.UtcNow;

        cb.Trip();

        cb.IsTripped.Should().BeTrue();
        cb.TrippedAt.Should().NotBeNull();
        cb.TrippedAt!.Value.Should().BeOnOrAfter(before);
        cb.TrippedAt!.Value.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Trip_WithError_SetsLastError()
    {
        var cb = new TemplateCircuitBreaker();

        cb.Trip("Something went wrong");

        cb.IsTripped.Should().BeTrue();
        cb.LastError.Should().Be("Something went wrong");
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var cb = new TemplateCircuitBreaker();
        cb.Trip("error");

        cb.Reset();

        cb.IsTripped.Should().BeFalse();
        cb.LastError.Should().BeNull();
        cb.TrippedAt.Should().BeNull();
    }

    [Fact]
    public void Reset_AfterTrip_AllowsReTripping()
    {
        var cb = new TemplateCircuitBreaker();
        var failures = new Dictionary<string, int>
        {
            ["t1"] = 3,
            ["t2"] = 3
        };

        // First trip
        cb.Evaluate(failures, threshold: 3).Should().BeTrue();
        cb.Trip();
        cb.IsTripped.Should().BeTrue();

        // After reset, Evaluate should return true again
        cb.Reset();
        cb.IsTripped.Should().BeFalse();
        cb.Evaluate(failures, threshold: 3).Should().BeTrue();
    }
}
