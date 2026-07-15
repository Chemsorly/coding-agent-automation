using CodingAgentWebUI.Pipeline.Models;
using MessagePack;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for IssueDetail.Images property (Key(4)).
/// Validates default value behavior and MessagePack serialization round-trip.
/// </summary>
public class IssueDetailImagesTests
{
    [Fact]
    public void Images_DefaultsToEmptyList_WhenNotExplicitlySet()
    {
        var detail = new IssueDetail
        {
            Description = "desc",
            Identifier = "org/repo#1",
            Labels = ["bug"],
            Title = "title"
        };

        Assert.NotNull(detail.Images);
        Assert.Empty(detail.Images);
    }

    [Fact]
    public void Images_CanBeSetWithImageReferences()
    {
        var images = new List<ImageReference>
        {
            new()
            {
                Url = "https://example.com/screenshot.png",
                AltText = "Screenshot",
                SourceType = ImageSourceType.Body,
                SourceIndex = 0
            }
        };

        var detail = new IssueDetail
        {
            Description = "desc",
            Identifier = "org/repo#2",
            Labels = [],
            Title = "title",
            Images = images
        };

        Assert.Single(detail.Images);
        Assert.Equal("https://example.com/screenshot.png", detail.Images[0].Url);
    }

    [Fact]
    public void Images_SurvivesMessagePackRoundTrip_WhenPopulated()
    {
        var original = new IssueDetail
        {
            Description = "Test description",
            Identifier = "org/repo#5",
            Labels = ["enhancement"],
            Title = "Test title",
            Images =
            [
                new ImageReference
                {
                    Url = "https://github.com/user/repo/assets/img1.png",
                    AltText = "Error dialog",
                    SourceType = ImageSourceType.Body,
                    SourceIndex = 0
                },
                new ImageReference
                {
                    Url = "https://github.com/user/repo/assets/img2.png",
                    AltText = "Console output",
                    SourceType = ImageSourceType.Comment,
                    SourceIndex = 1
                }
            ]
        };

        var bytes = MessagePackSerializer.Serialize(original);
        var deserialized = MessagePackSerializer.Deserialize<IssueDetail>(bytes);

        Assert.Equal(2, deserialized.Images.Count);
        Assert.Equal(original.Images[0].Url, deserialized.Images[0].Url);
        Assert.Equal(original.Images[0].AltText, deserialized.Images[0].AltText);
        Assert.Equal(original.Images[0].SourceType, deserialized.Images[0].SourceType);
        Assert.Equal(original.Images[1].Url, deserialized.Images[1].Url);
        Assert.Equal(original.Images[1].SourceType, deserialized.Images[1].SourceType);
    }

    [Fact]
    public void Images_SurvivesMessagePackRoundTrip_WhenEmpty()
    {
        var original = new IssueDetail
        {
            Description = "desc",
            Identifier = "org/repo#3",
            Labels = [],
            Title = "title"
            // Images not set — uses default []
        };

        var bytes = MessagePackSerializer.Serialize(original);
        var deserialized = MessagePackSerializer.Deserialize<IssueDetail>(bytes);

        Assert.NotNull(deserialized.Images);
        Assert.Empty(deserialized.Images);
    }

    [Fact]
    public void BackwardCompat_ConstructionWithoutImages_DefaultsToEmpty()
    {
        // The = [] default ensures that new code constructing IssueDetail
        // without explicitly setting Images gets an empty list (not null).
        // This is the backward compat guarantee: old agents that don't know
        // about Images won't break when they construct IssueDetail instances.
        var detail = new IssueDetail
        {
            Description = "description",
            Identifier = "org/repo#7",
            Labels = ["bug"],
            Title = "title"
        };

        Assert.NotNull(detail.Images);
        Assert.Empty(detail.Images);
        Assert.IsAssignableFrom<IReadOnlyList<ImageReference>>(detail.Images);
    }
}
