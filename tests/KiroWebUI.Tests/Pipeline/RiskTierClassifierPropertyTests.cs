using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;

namespace KiroWebUI.Tests.Pipeline;

/// <summary>
/// Property-based tests for RiskTierClassifier.
/// </summary>
public class RiskTierClassifierPropertyTests
{
    /// <summary>
    /// Null config always produces "standard" regardless of diff size or paths.
    /// </summary>
    [Property(MaxTest = 50)]
    public void NullConfig_AlwaysReturnsStandard(NonNegativeInt fileCount, NonNegativeInt lineCount)
    {
        var result = RiskTierClassifier.Classify(null, fileCount.Get, lineCount.Get, ["src/auth/handler.cs"]);
        result.Should().Be(RiskTierClassifier.Standard);
    }

    /// <summary>
    /// Security path match always produces "full" regardless of diff size.
    /// </summary>
    [Property(MaxTest = 50)]
    public void SecurityPathMatch_AlwaysReturnsFull(NonNegativeInt fileCount, NonNegativeInt lineCount)
    {
        var tiers = new CodeReviewRiskTiers
        {
            Skip = new RiskThreshold { MaxFiles = 1000, MaxLines = 10000 },
            SecurityPaths = ["auth/"]
        };
        var result = RiskTierClassifier.Classify(tiers, fileCount.Get, lineCount.Get, ["src/auth/handler.cs"]);
        result.Should().Be(RiskTierClassifier.Full);
    }

    /// <summary>
    /// Result is always one of the three valid tier values.
    /// </summary>
    [Property(MaxTest = 100)]
    public void Result_IsAlwaysValidTier(NonNegativeInt fileCount, NonNegativeInt lineCount, bool hasRiskTiers)
    {
        var tiers = hasRiskTiers
            ? new CodeReviewRiskTiers
            {
                Skip = new RiskThreshold { MaxFiles = 5, MaxLines = 30 },
                SecurityPaths = ["auth/"]
            }
            : null;
        var paths = fileCount.Get > 0 ? new[] { "src/Foo.cs" } : Array.Empty<string>();
        var result = RiskTierClassifier.Classify(tiers, fileCount.Get, lineCount.Get, paths);
        result.Should().BeOneOf(RiskTierClassifier.Skip, RiskTierClassifier.Standard, RiskTierClassifier.Full);
    }
}
