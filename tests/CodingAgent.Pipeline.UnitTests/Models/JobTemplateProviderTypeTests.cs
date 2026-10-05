using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Unit tests for <see cref="JobTemplateProviderType"/> — the single shared helper for
/// agent-backend provider-type comparisons (issue #3368).
///
/// Strategy: verify case-insensitive matching for exact, upper-case, and mixed-case inputs;
/// verify non-matches for empty, null, and other-backend inputs. This locks in the
/// OrdinalIgnoreCase semantics so a future accidental change to Ordinal or a constant
/// value change would be caught immediately.
/// </summary>
public class JobTemplateProviderTypeTests
{
    // ── IsKiro — positive cases ──────────────────────────────────────────────

    [Theory]
    [InlineData("kiro")]
    [InlineData("KIRO")]
    [InlineData("Kiro")]
    public void IsKiro_KiroVariants_ReturnsTrue(string providerType)
        => JobTemplateProviderType.IsKiro(providerType).Should().BeTrue(
            $"'{providerType}' is a case variant of the kiro provider type");

    // ── IsKiro — negative cases ──────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("opencode")]
    [InlineData("kiro-dotnet")]
    public void IsKiro_NonKiroInputs_ReturnsFalse(string providerType)
        => JobTemplateProviderType.IsKiro(providerType).Should().BeFalse(
            $"'{providerType}' does not match the kiro provider type");

    [Fact]
    public void IsKiro_Null_ReturnsFalse()
        => JobTemplateProviderType.IsKiro(null).Should().BeFalse(
            "null is not a valid provider type string");

    // ── IsOpencode — positive cases ──────────────────────────────────────────

    [Theory]
    [InlineData("opencode")]
    [InlineData("OPENCODE")]
    [InlineData("OpenCode")]
    public void IsOpencode_OpencodeVariants_ReturnsTrue(string providerType)
        => JobTemplateProviderType.IsOpencode(providerType).Should().BeTrue(
            $"'{providerType}' is a case variant of the opencode provider type");

    // ── IsOpencode — negative cases ──────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("kiro")]
    [InlineData("opencode-dotnet")]
    public void IsOpencode_NonOpencodeInputs_ReturnsFalse(string providerType)
        => JobTemplateProviderType.IsOpencode(providerType).Should().BeFalse(
            $"'{providerType}' does not match the opencode provider type");

    [Fact]
    public void IsOpencode_Null_ReturnsFalse()
        => JobTemplateProviderType.IsOpencode(null).Should().BeFalse(
            "null is not a valid provider type string");
}
