using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Unit tests for <see cref="InlineCommentSettings"/> deserialization and configuration behavior.
/// Validates Req 8: Inline Review Settings Configuration.
/// </summary>
public class InlineCommentSettingsTests
{
    [Fact]
    public void Deserialization_EmptyJson_ProducesAllDefaults()
    {
        var settings = JsonSerializer.Deserialize<InlineCommentSettings>("{}");

        settings.Should().NotBeNull();
        settings!.Enabled.Should().BeTrue();
        settings.SeverityThreshold.Should().Be(FindingSeverity.Warning);
        settings.MaxInlineComments.Should().Be(15);
        settings.OrderBySeverity.Should().BeTrue();
        settings.MaxRetries.Should().Be(1);
    }

    [Fact]
    public void Deserialization_ExplicitValues_CorrectlyDeserialized()
    {
        var json = """
        {
            "Enabled": true,
            "SeverityThreshold": "Critical",
            "MaxInlineComments": 30,
            "OrderBySeverity": false,
            "MaxRetries": 3
        }
        """;

        var settings = JsonSerializer.Deserialize<InlineCommentSettings>(json);

        settings.Should().NotBeNull();
        settings!.Enabled.Should().BeTrue();
        settings.SeverityThreshold.Should().Be(FindingSeverity.Critical);
        settings.MaxInlineComments.Should().Be(30);
        settings.OrderBySeverity.Should().BeFalse();
        settings.MaxRetries.Should().Be(3);
    }

    [Fact]
    public void Serialization_SeverityThreshold_ProducesHumanReadableString()
    {
        var settings = new InlineCommentSettings { SeverityThreshold = FindingSeverity.Critical };

        var json = JsonSerializer.Serialize(settings);

        json.Should().Contain("\"Critical\"");
        json.Should().NotContain("\"2\"");
    }

    [Fact]
    public void Serialization_SeverityThreshold_Warning_ProducesString()
    {
        var settings = new InlineCommentSettings { SeverityThreshold = FindingSeverity.Warning };

        var json = JsonSerializer.Serialize(settings);

        json.Should().Contain("\"Warning\"");
    }

    [Fact]
    public void Serialization_SeverityThreshold_Suggestion_ProducesString()
    {
        var settings = new InlineCommentSettings { SeverityThreshold = FindingSeverity.Suggestion };

        var json = JsonSerializer.Serialize(settings);

        json.Should().Contain("\"Suggestion\"");
    }

    [Fact]
    public void CodeReviewConfiguration_WithoutInlineCommentsKey_DefaultsCorrectly()
    {
        var json = """
        {
            "MaxIterations": 5
        }
        """;

        var config = JsonSerializer.Deserialize<CodeReviewConfiguration>(json);

        config.Should().NotBeNull();
        config!.MaxIterations.Should().Be(5);
        config.InlineComments.Should().NotBeNull();
        config.InlineComments.Enabled.Should().BeTrue();
        config.InlineComments.SeverityThreshold.Should().Be(FindingSeverity.Warning);
        config.InlineComments.MaxInlineComments.Should().Be(15);
        config.InlineComments.OrderBySeverity.Should().BeTrue();
        config.InlineComments.MaxRetries.Should().Be(1);
    }

    [Fact]
    public void CodeReviewConfiguration_WithLegacyReviewIsolation_IgnoresFieldGracefully()
    {
        // Backward compatibility: old JSON configs may still contain ReviewIsolation.
        // System.Text.Json silently ignores unknown properties.
        var json = """
        {
            "MaxIterations": 3,
            "ReviewIsolation": "Shared"
        }
        """;

        var config = JsonSerializer.Deserialize<CodeReviewConfiguration>(json);

        config.Should().NotBeNull();
        config!.MaxIterations.Should().Be(3);
        config.InlineComments.Should().NotBeNull();
    }

    [Fact]
    public void CodeReviewConfiguration_WithInlineCommentsKey_DeserializesCorrectly()
    {
        var json = """
        {
            "MaxIterations": 3,
            "InlineComments": {
                "Enabled": true,
                "SeverityThreshold": "Suggestion",
                "MaxInlineComments": 50,
                "OrderBySeverity": true,
                "MaxRetries": 5
            }
        }
        """;

        var config = JsonSerializer.Deserialize<CodeReviewConfiguration>(json);

        config.Should().NotBeNull();
        config!.MaxIterations.Should().Be(3);
        config.InlineComments.Enabled.Should().BeTrue();
        config.InlineComments.SeverityThreshold.Should().Be(FindingSeverity.Suggestion);
        config.InlineComments.MaxInlineComments.Should().Be(50);
        config.InlineComments.OrderBySeverity.Should().BeTrue();
        config.InlineComments.MaxRetries.Should().Be(5);
    }

    [Fact]
    public void DefaultInstance_HasExpectedValues()
    {
        var settings = new InlineCommentSettings();

        settings.Enabled.Should().BeTrue();
        settings.SeverityThreshold.Should().Be(FindingSeverity.Warning);
        settings.MaxInlineComments.Should().Be(15);
        settings.OrderBySeverity.Should().BeTrue();
        settings.MaxRetries.Should().Be(1);
    }

    [Fact]
    public void Deserialization_CaseInsensitiveSeverity_Works()
    {
        var json = """{"SeverityThreshold": "warning"}""";

        var settings = JsonSerializer.Deserialize<InlineCommentSettings>(json);

        settings.Should().NotBeNull();
        settings!.SeverityThreshold.Should().Be(FindingSeverity.Warning);
    }
}


// ── ApplyOverrides tests ──────────────────────────────────────────────────────

public class InlineCommentSettingsApplyOverridesTests
{
    [Fact]
    public void ApplyOverrides_AllNull_ReturnsIdentical()
    {
        var settings = new InlineCommentSettings
        {
            Enabled = false,
            MaxInlineComments = 10,
            MaxRetries = 3,
            OrderBySeverity = false,
            SeverityThreshold = FindingSeverity.Critical
        };

        var result = settings.ApplyOverrides(new InlineCommentOverrides());

        result.Should().Be(settings, "all-null overrides must return a semantically identical record");
    }

    [Fact]
    public void ApplyOverrides_AllSet_ReplacesAllFields()
    {
        var original = new InlineCommentSettings
        {
            Enabled = true,
            MaxInlineComments = 15,
            MaxRetries = 1,
            OrderBySeverity = true,
            SeverityThreshold = FindingSeverity.Warning
        };

        var overrides = new InlineCommentOverrides
        {
            Enabled = false,
            MaxInlineComments = 5,
            MaxRetries = 0,
            OrderBySeverity = false,
            SeverityThreshold = FindingSeverity.Critical
        };

        var result = original.ApplyOverrides(overrides);

        result.Enabled.Should().BeFalse();
        result.MaxInlineComments.Should().Be(5);
        result.MaxRetries.Should().Be(0);
        result.OrderBySeverity.Should().BeFalse();
        result.SeverityThreshold.Should().Be(FindingSeverity.Critical);
    }

    [Fact]
    public void ApplyOverrides_PartialSet_OnlyOverridesNonNull()
    {
        var original = new InlineCommentSettings
        {
            Enabled = true,
            MaxInlineComments = 15,
            MaxRetries = 1,
            OrderBySeverity = true,
            SeverityThreshold = FindingSeverity.Warning
        };

        // Only override MaxInlineComments and SeverityThreshold
        var result = original.ApplyOverrides(new InlineCommentOverrides
        {
            MaxInlineComments = 30,
            SeverityThreshold = FindingSeverity.Critical
        });

        result.Enabled.Should().BeTrue("null Enabled override must preserve original");
        result.MaxInlineComments.Should().Be(30, "non-null MaxInlineComments must be applied");
        result.MaxRetries.Should().Be(1, "null MaxRetries override must preserve original");
        result.OrderBySeverity.Should().BeTrue("null OrderBySeverity override must preserve original");
        result.SeverityThreshold.Should().Be(FindingSeverity.Critical, "non-null SeverityThreshold must be applied");
    }

    [Fact]
    public void ApplyOverrides_IsIdempotent()
    {
        // Applying the same non-null overrides twice must produce the same result as applying once
        var original = new InlineCommentSettings { MaxInlineComments = 10 };
        var overrides = new InlineCommentOverrides { MaxInlineComments = 20 };

        var once = original.ApplyOverrides(overrides);
        var twice = once.ApplyOverrides(overrides);

        twice.MaxInlineComments.Should().Be(once.MaxInlineComments,
            "idempotence: applying same override twice must produce same result as once");
    }

    [Fact]
    public void ApplyOverrides_Enabled_FalseOverride()
    {
        var settings = new InlineCommentSettings { Enabled = true };
        var result = settings.ApplyOverrides(new InlineCommentOverrides { Enabled = false });
        result.Enabled.Should().BeFalse("explicit false must override true");
    }

    [Fact]
    public void ApplyOverrides_Enabled_TrueOverride()
    {
        var settings = new InlineCommentSettings { Enabled = false };
        var result = settings.ApplyOverrides(new InlineCommentOverrides { Enabled = true });
        result.Enabled.Should().BeTrue("explicit true must override false");
    }
}

// ── InlineCommentSettings MessagePack roundtrip ──────────────────────────────

public class InlineCommentSettingsMessagePackRoundtripTests
{
    private static readonly MessagePack.MessagePackSerializerOptions MsgPackOptions =
        MessagePack.Resolvers.ContractlessStandardResolverAllowPrivate.Options;

    private static T RoundTrip<T>(T original)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(original, MsgPackOptions);
        return MessagePack.MessagePackSerializer.Deserialize<T>(bytes, MsgPackOptions);
    }

    [Theory]
    [InlineData(true, 5, 0, false, FindingSeverity.Suggestion)]
    [InlineData(false, 50, 5, true, FindingSeverity.Critical)]
    [InlineData(true, 15, 1, true, FindingSeverity.Warning)]
    public void InlineCommentSettings_RoundTrip_PreservesAllFields(
        bool enabled, int maxComments, int maxRetries, bool orderBySeverity, FindingSeverity threshold)
    {
        var original = new InlineCommentSettings
        {
            Enabled = enabled,
            MaxInlineComments = maxComments,
            MaxRetries = maxRetries,
            OrderBySeverity = orderBySeverity,
            SeverityThreshold = threshold
        };

        var d = RoundTrip(original);

        d.Enabled.Should().Be(original.Enabled);
        d.MaxInlineComments.Should().Be(original.MaxInlineComments);
        d.MaxRetries.Should().Be(original.MaxRetries);
        d.OrderBySeverity.Should().Be(original.OrderBySeverity);
        d.SeverityThreshold.Should().Be(original.SeverityThreshold);
    }

    [Fact]
    public void InlineCommentSettings_DefaultValues_SurviveRoundTrip()
    {
        var original = new InlineCommentSettings();
        var d = RoundTrip(original);

        d.Enabled.Should().BeTrue();
        d.MaxInlineComments.Should().Be(15);
        d.MaxRetries.Should().Be(1);
        d.OrderBySeverity.Should().BeTrue();
        d.SeverityThreshold.Should().Be(FindingSeverity.Warning);
    }
}
