using AwesomeAssertions;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;

namespace KiroWebUI.Tests.Pipeline;

public class RiskTierClassifierTests
{
    [Fact]
    public void Classify_NullRiskTiers_ReturnsStandard()
    {
        var result = RiskTierClassifier.Classify(null, 10, 100, ["src/Foo.cs"]);
        result.Should().Be(RiskTierClassifier.Standard);
    }

    [Fact]
    public void Classify_BelowSkipThreshold_ReturnsSkip()
    {
        var tiers = new CodeReviewRiskTiers
        {
            Skip = new RiskThreshold { MaxFiles = 5, MaxLines = 30 }
        };
        var result = RiskTierClassifier.Classify(tiers, 3, 20, ["src/Foo.cs", "src/Bar.cs", "src/Baz.cs"]);
        result.Should().Be(RiskTierClassifier.Skip);
    }

    [Fact]
    public void Classify_AboveSkipThresholdOnFiles_ReturnsStandard()
    {
        var tiers = new CodeReviewRiskTiers
        {
            Skip = new RiskThreshold { MaxFiles = 5, MaxLines = 30 }
        };
        var result = RiskTierClassifier.Classify(tiers, 6, 20, ["a", "b", "c", "d", "e", "f"]);
        result.Should().Be(RiskTierClassifier.Standard);
    }

    [Fact]
    public void Classify_AboveSkipThresholdOnLines_ReturnsStandard()
    {
        var tiers = new CodeReviewRiskTiers
        {
            Skip = new RiskThreshold { MaxFiles = 5, MaxLines = 30 }
        };
        var result = RiskTierClassifier.Classify(tiers, 2, 31, ["a", "b"]);
        result.Should().Be(RiskTierClassifier.Standard);
    }

    [Fact]
    public void Classify_ExactlyAtSkipThreshold_ReturnsSkip()
    {
        var tiers = new CodeReviewRiskTiers
        {
            Skip = new RiskThreshold { MaxFiles = 5, MaxLines = 30 }
        };
        var result = RiskTierClassifier.Classify(tiers, 5, 30, ["a", "b", "c", "d", "e"]);
        result.Should().Be(RiskTierClassifier.Skip);
    }

    [Fact]
    public void Classify_SecurityPathMatch_ReturnsFull()
    {
        var tiers = new CodeReviewRiskTiers
        {
            Skip = new RiskThreshold { MaxFiles = 5, MaxLines = 30 },
            SecurityPaths = ["auth/", "crypto/"]
        };
        var result = RiskTierClassifier.Classify(tiers, 1, 5, ["src/auth/handler.cs"]);
        result.Should().Be(RiskTierClassifier.Full);
    }

    [Fact]
    public void Classify_SecurityPathMatch_OverridesSmallDiff()
    {
        var tiers = new CodeReviewRiskTiers
        {
            Skip = new RiskThreshold { MaxFiles = 100, MaxLines = 1000 },
            SecurityPaths = [".env"]
        };
        var result = RiskTierClassifier.Classify(tiers, 1, 1, [".env.production"]);
        result.Should().Be(RiskTierClassifier.Full);
    }

    [Fact]
    public void Classify_SecurityPathMatch_CaseInsensitive()
    {
        var tiers = new CodeReviewRiskTiers
        {
            SecurityPaths = ["Auth/"]
        };
        var result = RiskTierClassifier.Classify(tiers, 1, 5, ["src/auth/Login.cs"]);
        result.Should().Be(RiskTierClassifier.Full);
    }

    [Fact]
    public void Classify_SecurityPathMatch_BackslashNormalized()
    {
        var tiers = new CodeReviewRiskTiers
        {
            SecurityPaths = ["auth/"]
        };
        var result = RiskTierClassifier.Classify(tiers, 1, 5, ["src\\auth\\Login.cs"]);
        result.Should().Be(RiskTierClassifier.Full);
    }

    [Fact]
    public void Classify_EmptySecurityPaths_NoForcedFull()
    {
        var tiers = new CodeReviewRiskTiers
        {
            Skip = new RiskThreshold { MaxFiles = 5, MaxLines = 30 },
            SecurityPaths = []
        };
        var result = RiskTierClassifier.Classify(tiers, 1, 5, ["src/auth/handler.cs"]);
        result.Should().Be(RiskTierClassifier.Skip);
    }

    [Fact]
    public void Classify_NullSkipThreshold_NeverSkips()
    {
        var tiers = new CodeReviewRiskTiers
        {
            Skip = null,
            SecurityPaths = ["auth/"]
        };
        // Small diff but no skip threshold → standard (not skip)
        var result = RiskTierClassifier.Classify(tiers, 1, 1, ["src/Foo.cs"]);
        result.Should().Be(RiskTierClassifier.Standard);
    }

    [Fact]
    public void Classify_ZeroFilesZeroLines_BelowThreshold_ReturnsSkip()
    {
        var tiers = new CodeReviewRiskTiers
        {
            Skip = new RiskThreshold { MaxFiles = 5, MaxLines = 30 }
        };
        var result = RiskTierClassifier.Classify(tiers, 0, 0, []);
        result.Should().Be(RiskTierClassifier.Skip);
    }

    [Fact]
    public void Classify_MultipleSecurityPaths_OneMatches_ReturnsFull()
    {
        var tiers = new CodeReviewRiskTiers
        {
            SecurityPaths = ["auth/", "crypto/", "credentials", ".env", "appsettings"]
        };
        var result = RiskTierClassifier.Classify(tiers, 1, 5, ["config/appsettings.json"]);
        result.Should().Be(RiskTierClassifier.Full);
    }

    [Fact]
    public void Classify_NoSecurityPathMatch_LargeDiff_ReturnsStandard()
    {
        var tiers = new CodeReviewRiskTiers
        {
            Skip = new RiskThreshold { MaxFiles = 5, MaxLines = 30 },
            SecurityPaths = ["auth/", "crypto/"]
        };
        var result = RiskTierClassifier.Classify(tiers, 10, 500, ["src/Foo.cs", "src/Bar.cs"]);
        result.Should().Be(RiskTierClassifier.Standard);
    }

    [Fact]
    public void GetMatchedSecurityPaths_ReturnsMatchedPaths()
    {
        var secPaths = new[] { "auth/", "crypto/", ".env" };
        var changedFiles = new[] { "src/auth/handler.cs", "src/utils.cs", ".env.local" };

        var matched = RiskTierClassifier.GetMatchedSecurityPaths(secPaths, changedFiles);
        matched.Should().HaveCount(2);
        matched.Should().Contain("auth/");
        matched.Should().Contain(".env");
    }

    [Fact]
    public void GetMatchedSecurityPaths_NullSecurityPaths_ReturnsEmpty()
    {
        var matched = RiskTierClassifier.GetMatchedSecurityPaths(null, ["src/auth/handler.cs"]);
        matched.Should().BeEmpty();
    }

    [Fact]
    public void GetMatchedSecurityPaths_EmptySecurityPaths_ReturnsEmpty()
    {
        var matched = RiskTierClassifier.GetMatchedSecurityPaths([], ["src/auth/handler.cs"]);
        matched.Should().BeEmpty();
    }
}
