using CodingAgentWebUI.Pipeline.Models;
using AwesomeAssertions;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class PipelineConfigurationImageSettingsTests
{
    [Fact]
    public void MaxIssueImages_DefaultsTo10()
    {
        var config = new PipelineConfiguration();
        config.MaxIssueImages.Should().Be(10);
    }

    [Fact]
    public void MaxImageSizeBytes_DefaultsTo5MB()
    {
        var config = new PipelineConfiguration();
        config.MaxImageSizeBytes.Should().Be(5_242_880);
    }

    [Fact]
    public void MaxTotalImageSizeBytes_DefaultsTo20MB()
    {
        var config = new PipelineConfiguration();
        config.MaxTotalImageSizeBytes.Should().Be(20_971_520);
    }

    [Fact]
    public void TotalImageDownloadTimeoutSeconds_DefaultsTo60()
    {
        var config = new PipelineConfiguration();
        config.TotalImageDownloadTimeoutSeconds.Should().Be(60);
    }

    [Fact]
    public void EnableIssueImageExtraction_DefaultsToTrue()
    {
        var config = new PipelineConfiguration();
        config.EnableIssueImageExtraction.Should().BeTrue();
    }

    [Fact]
    public void EnableNativeImageParts_DefaultsToTrue()
    {
        var config = new PipelineConfiguration();
        config.EnableNativeImageParts.Should().BeTrue();
    }

    [Fact]
    public void ImageDownloadTimeoutSeconds_DefaultsTo30()
    {
        var config = new PipelineConfiguration();
        config.ImageDownloadTimeoutSeconds.Should().Be(30);
    }

    [Fact]
    public void ImageSettings_CanBeOverriddenViaInit()
    {
        var config = new PipelineConfiguration
        {
            MaxIssueImages = 5,
            MaxImageSizeBytes = 1_000_000,
            MaxTotalImageSizeBytes = 10_000_000,
            TotalImageDownloadTimeoutSeconds = 30,
            EnableIssueImageExtraction = false,
            EnableNativeImageParts = false,
            ImageDownloadTimeoutSeconds = 15
        };

        config.MaxIssueImages.Should().Be(5);
        config.MaxImageSizeBytes.Should().Be(1_000_000);
        config.MaxTotalImageSizeBytes.Should().Be(10_000_000);
        config.TotalImageDownloadTimeoutSeconds.Should().Be(30);
        config.EnableIssueImageExtraction.Should().BeFalse();
        config.EnableNativeImageParts.Should().BeFalse();
        config.ImageDownloadTimeoutSeconds.Should().Be(15);
    }
}
