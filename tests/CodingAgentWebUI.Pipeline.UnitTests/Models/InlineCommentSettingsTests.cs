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
            "MaxIterations": 5,
            "ReviewIsolation": "Shared"
        }
        """;

        var config = JsonSerializer.Deserialize<CodeReviewConfiguration>(json);

        config.Should().NotBeNull();
        config!.MaxIterations.Should().Be(5);
        config.ReviewIsolation.Should().Be(ReviewIsolation.Shared);
        config.InlineComments.Should().NotBeNull();
        config.InlineComments.Enabled.Should().BeTrue();
        config.InlineComments.SeverityThreshold.Should().Be(FindingSeverity.Warning);
        config.InlineComments.MaxInlineComments.Should().Be(15);
        config.InlineComments.OrderBySeverity.Should().BeTrue();
        config.InlineComments.MaxRetries.Should().Be(1);
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
