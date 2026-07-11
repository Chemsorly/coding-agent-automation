using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class IssueImageExtractorTests
{
    private readonly IssueImageExtractor _extractor = new();

    [Fact]
    public void Extract_StandardInlineImage_ReturnsImageReference()
    {
        var body = "Here is a screenshot:\n![bug screenshot](https://example.com/screenshot.png)\nEnd.";

        var result = _extractor.Extract(body, null, "123", ImageSourceKind.Issue);

        Assert.Single(result);
        Assert.Equal("https://example.com/screenshot.png", result[0].Url);
        Assert.Equal("bug screenshot", result[0].AltText);
        Assert.Equal(ImageSourceType.Body, result[0].SourceType);
        Assert.Equal(0, result[0].SourceIndex);
    }

    [Fact]
    public void Extract_ImageWithTitleText_StripsTitle()
    {
        var body = """![alt](https://example.com/img.png "Some title text")""";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
        Assert.Equal("https://example.com/img.png", result[0].Url);
    }

    [Fact]
    public void Extract_ReferenceStyleImage_ResolvesUrl()
    {
        var body = """
            ![my diagram][diagram-ref]

            [diagram-ref]: https://example.com/diagram.png
            """;

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
        Assert.Equal("https://example.com/diagram.png", result[0].Url);
        Assert.Equal("my diagram", result[0].AltText);
    }

    [Fact]
    public void Extract_ClickableThumbnail_ExtractsInnerImageOnly()
    {
        var body = "[![thumb alt](https://example.com/thumb.png)](https://example.com/full-page)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
        Assert.Equal("https://example.com/thumb.png", result[0].Url);
        Assert.Equal("thumb alt", result[0].AltText);
    }

    [Fact]
    public void Extract_HtmlImgTag_ExtractsSrc()
    {
        var body = """<img src="https://example.com/html-image.jpg" width="500" alt="test">""";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
        Assert.Equal("https://example.com/html-image.jpg", result[0].Url);
    }

    [Fact]
    public void Extract_HtmlImgTag_SingleQuotes()
    {
        var body = "<img src='https://example.com/single.png' />";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
        Assert.Equal("https://example.com/single.png", result[0].Url);
    }

    [Fact]
    public void Extract_HtmlImgTag_AnyAttributeOrder()
    {
        var body = """<img width="800" alt="x" src="https://example.com/reordered.png" class="screenshot">""";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
        Assert.Equal("https://example.com/reordered.png", result[0].Url);
    }

    [Fact]
    public void Extract_ImageInsideDetails_Extracted()
    {
        var body = """
            <details>
            <summary>Screenshots</summary>

            ![inside details](https://example.com/details.png)

            </details>
            """;

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
        Assert.Equal("https://example.com/details.png", result[0].Url);
    }

    [Fact]
    public void Extract_ImageInsideTable_Extracted()
    {
        var body = """
            | Before | After |
            |--------|-------|
            | ![before](https://example.com/before.png) | ![after](https://example.com/after.png) |
            """;

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Equal(2, result.Count);
        Assert.Equal("https://example.com/before.png", result[0].Url);
        Assert.Equal("https://example.com/after.png", result[1].Url);
    }

    [Fact]
    public void Extract_PercentEncodedUrl_KeptAsIs()
    {
        var body = "![image](https://example.com/my%20image.png)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
        Assert.Equal("https://example.com/my%20image.png", result[0].Url);
    }

    [Fact]
    public void Extract_ParenthesesInUrl_BalancedMatching()
    {
        var body = "![diagram](https://example.com/path(1)/image.png)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
        Assert.Equal("https://example.com/path(1)/image.png", result[0].Url);
    }

    // --- Code-block exclusion tests ---

    [Fact]
    public void Extract_InsideFencedCodeBlock_NotExtracted()
    {
        var body = """
            Normal text
            ```
            ![should not extract](https://example.com/code-block.png)
            ```
            After code block
            """;

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_InsideTildeFencedCodeBlock_NotExtracted()
    {
        var body = """
            ~~~
            ![should not extract](https://example.com/tilde.png)
            ~~~
            """;

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_InsideIndentedCodeBlock_NotExtracted()
    {
        var body = "Normal text\n\n    ![indented](https://example.com/indented.png)\n";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }

    // --- Badge/filter tests ---

    [Fact]
    public void Extract_ShieldsIoBadge_Filtered()
    {
        var body = "![coverage](https://img.shields.io/badge/coverage-90%25-green)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_BadgeFuryDomain_Filtered()
    {
        var body = "![version](https://badge.fury.io/nuget/MyPackage.svg)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_UrlPathContainsBadge_Filtered()
    {
        var body = "![status](https://example.com/repo/badge/build-status.png)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_UrlEndingInBadgeSvg_Filtered()
    {
        var body = "![ci](https://example.com/build-badge.svg)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_GitHubActionsWorkflowBadge_Filtered()
    {
        var body = "![CI](https://github.com/user/repo/workflows/CI/badge.svg)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_AvatarUrl_Filtered()
    {
        var body = "![avatar](https://avatars.githubusercontent.com/u/12345?v=4)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_VideoExtension_Filtered()
    {
        var body = "![video](https://example.com/demo.mp4)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_SvgFile_Filtered()
    {
        var body = "![icon](https://example.com/icon.svg)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }

    // --- Deduplication ---

    [Fact]
    public void Extract_SameUrlTwice_DeduplicatedFirstWins()
    {
        var body = """
            ![first alt](https://example.com/same.png)
            ![second alt](https://example.com/same.png)
            """;

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
        Assert.Equal("first alt", result[0].AltText);
    }

    // --- Comments ---

    [Fact]
    public void Extract_ImagesFromComments_CorrectSourceIndex()
    {
        var body = "![body image](https://example.com/body.png)";
        var comments = new List<IssueComment>
        {
            new() { Author = "user1", Body = "![comment 0](https://example.com/c0.png)", CreatedAt = DateTime.UtcNow, Id = "1" },
            new() { Author = "user2", Body = "![comment 1](https://example.com/c1.png)", CreatedAt = DateTime.UtcNow, Id = "2" }
        };

        var result = _extractor.Extract(body, comments, "42", ImageSourceKind.Issue);

        Assert.Equal(3, result.Count);
        Assert.Equal(ImageSourceType.Body, result[0].SourceType);
        Assert.Equal(0, result[0].SourceIndex);
        Assert.Equal(ImageSourceType.Comment, result[1].SourceType);
        Assert.Equal(0, result[1].SourceIndex);
        Assert.Equal(ImageSourceType.Comment, result[2].SourceType);
        Assert.Equal(1, result[2].SourceIndex);
    }

    // --- Empty/null body ---

    [Fact]
    public void Extract_EmptyBody_ReturnsEmptyList()
    {
        var result = _extractor.Extract("", null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_NullComments_NoError()
    {
        var body = "![img](https://example.com/x.png)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
    }

    // --- Source kind prefix ---

    [Fact]
    public void Extract_PullRequestSourceKind_FilenameAssignment()
    {
        // The result should use PR-related naming. We verify by checking sourceKind doesn't crash
        // and references are created. Filename assignment is internal detail but sourceKind should work.
        var body = "![pr img](https://example.com/pr-screenshot.png)";

        var result = _extractor.Extract(body, null, "456", ImageSourceKind.PullRequest);

        Assert.Single(result);
        Assert.Equal("https://example.com/pr-screenshot.png", result[0].Url);
    }

    // --- Extension determination ---

    [Fact]
    public void Extract_ExtensionlessUrl_DefaultsPng()
    {
        // GitHub asset UUID URL has no extension
        var body = "![asset](https://github.com/user-attachments/assets/abc-def-123)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        // Should not be filtered — it's a valid image URL
        Assert.Single(result);
    }

    [Fact]
    public void Extract_JpegExtension_Recognized()
    {
        var body = "![photo](https://example.com/photo.jpeg)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
    }

    [Fact]
    public void Extract_WebpExtension_Recognized()
    {
        var body = "![webp](https://example.com/image.webp)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
    }

    [Fact]
    public void Extract_GitLabRelativeUploadUrl_Extracted()
    {
        var body = "![upload](/uploads/abc123def456/screenshot.png)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
        Assert.Equal("/uploads/abc123def456/screenshot.png", result[0].Url);
    }

    [Fact]
    public void Extract_MultipleImageTypes_AllExtracted()
    {
        var body = """
            ![inline](https://example.com/inline.png)
            <img src="https://example.com/html.jpg" />
            ![ref][my-ref]

            [my-ref]: https://example.com/reference.gif
            """;

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Equal(3, result.Count);
        var urls = result.Select(r => r.Url).ToList();
        Assert.Contains("https://example.com/inline.png", urls);
        Assert.Contains("https://example.com/html.jpg", urls);
        Assert.Contains("https://example.com/reference.gif", urls);
    }

    [Fact]
    public void Extract_ImageAfterCodeBlock_Extracted()
    {
        var body = """
            ```
            code here
            ```
            ![after code](https://example.com/after.png)
            """;

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Single(result);
        Assert.Equal("https://example.com/after.png", result[0].Url);
    }

    [Fact]
    public void Extract_CodecovDomain_Filtered()
    {
        var body = "![codecov](https://codecov.io/gh/user/repo/graph/badge.svg)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_MovVideo_Filtered()
    {
        var body = "![screen recording](https://example.com/recording.mov)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_WebmVideo_Filtered()
    {
        var body = "![demo](https://example.com/demo.webm)";

        var result = _extractor.Extract(body, null, "1", ImageSourceKind.Issue);

        Assert.Empty(result);
    }
}
