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

    // TODO: This test relies on the assumption that <5 minutes elapses between Trip() and ShouldAutoResume().
    // Consider injecting a TimeProvider to test exact boundary conditions deterministically.
    [Fact]
    public void ShouldAutoResume_BeforeCooldown_ReturnsFalse()
    {
        var cb = new TemplateCircuitBreaker();
        cb.Trip();

        // Cooldown is 5 minutes — we just tripped, so it shouldn't auto-resume
        cb.ShouldAutoResume(TimeSpan.FromMinutes(5)).Should().BeFalse();
    }

    // TODO: TimeSpan.Zero makes this assertion trivially true (any elapsed time >= Zero).
    // This would pass even if ShouldAutoResume were implemented as `return IsTripped;`.
    // Consider using a TimeProvider to test actual cooldown boundary logic.
    [Fact]
    public void ShouldAutoResume_AfterCooldown_ReturnsTrue()
    {
        var cb = new TemplateCircuitBreaker();
        cb.Trip();

        // Use a zero cooldown — should immediately be ready to resume
        cb.ShouldAutoResume(TimeSpan.Zero).Should().BeTrue();
    }

    [Fact]
    public void ShouldAutoResume_WhenNotTripped_ReturnsFalse()
    {
        var cb = new TemplateCircuitBreaker();

        cb.ShouldAutoResume(TimeSpan.Zero).Should().BeFalse();
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
