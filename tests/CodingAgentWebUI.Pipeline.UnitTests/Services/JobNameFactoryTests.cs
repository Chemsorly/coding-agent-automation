using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Property-based tests for <see cref="JobNameFactory"/> — the authoritative source for all
/// K8s Job naming formats.
/// </summary>
public class JobNameFactoryTests
{
    // ─── ForWorkItem ──────────────────────────────────────────────────────────

    /// <summary>
    /// For any GUID, ForWorkItem produces caa-agent-{first-11-hex-chars} = 21 chars.
    /// </summary>
    [Property(MaxTest = 20)]
    public void ForWorkItem_MatchesDeterministicFormula(Guid id)
    {
        var expected = $"caa-agent-{id:N}"[..21];

        var actual = JobNameFactory.ForWorkItem(id);

        actual.Should().Be(expected);
    }

    // ─── ForConsolidation ─────────────────────────────────────────────────────

    /// <summary>
    /// For any GUID, ForConsolidation produces caa-cons-{first-12-hex-chars} = 21 chars.
    /// </summary>
    [Property(MaxTest = 20)]
    public void ForConsolidation_MatchesDeterministicFormula(Guid id)
    {
        var expected = $"caa-cons-{id:N}"[..21];

        var actual = JobNameFactory.ForConsolidation(id);

        actual.Should().Be(expected);
    }

    // ─── ForBrain ─────────────────────────────────────────────────────────────

    /// <summary>
    /// For any GUID, ForBrain produces "caa-" + first-8-hex-chars = 12 chars.
    /// The assertion form matches DispatchServiceJobNamingPropertyTests for equivalence verification.
    /// </summary>
    [Property(MaxTest = 20)]
    public void ForBrain_MatchesDeterministicFormula(Guid id)
    {
        var expected = "caa-" + id.ToString("N")[..8];

        var actual = JobNameFactory.ForBrain(id);

        actual.Should().Be(expected);
    }

    // ─── Length invariants ────────────────────────────────────────────────────

    [Property(MaxTest = 20)]
    public void ForWorkItem_ProducesLength21(Guid id) =>
        JobNameFactory.ForWorkItem(id).Length.Should().Be(21);

    [Property(MaxTest = 20)]
    public void ForConsolidation_ProducesLength21(Guid id) =>
        JobNameFactory.ForConsolidation(id).Length.Should().Be(21);

    [Property(MaxTest = 20)]
    public void ForBrain_ProducesLength12(Guid id) =>
        JobNameFactory.ForBrain(id).Length.Should().Be(12);

    // ─── Distinctness guard ───────────────────────────────────────────────────

    /// <summary>
    /// All three formats produce different strings for the same ID.
    /// Prevents accidental delegation to the wrong factory method.
    /// </summary>
    [Fact]
    public void AllThreeFormats_AreDistinct_ForSameId()
    {
        var id = Guid.Parse("12345678-1234-1234-1234-1234567890ab");

        var forWorkItem = JobNameFactory.ForWorkItem(id);
        var forConsolidation = JobNameFactory.ForConsolidation(id);
        var forBrain = JobNameFactory.ForBrain(id);

        forWorkItem.Should().NotBe(forConsolidation);
        forWorkItem.Should().NotBe(forBrain);
        forConsolidation.Should().NotBe(forBrain);
    }

    // ─── Prefix guards ────────────────────────────────────────────────────────

    [Property(MaxTest = 20)]
    public void ForWorkItem_StartsWithExpectedPrefix(Guid id) =>
        JobNameFactory.ForWorkItem(id).Should().StartWith("caa-agent-");

    [Property(MaxTest = 20)]
    public void ForConsolidation_StartsWithExpectedPrefix(Guid id) =>
        JobNameFactory.ForConsolidation(id).Should().StartWith("caa-cons-");

    [Property(MaxTest = 20)]
    public void ForBrain_StartsWithExpectedPrefix(Guid id) =>
        JobNameFactory.ForBrain(id).Should().StartWith("caa-");
}
