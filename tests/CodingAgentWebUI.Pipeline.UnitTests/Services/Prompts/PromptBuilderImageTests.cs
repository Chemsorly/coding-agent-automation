using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services.Prompts;

/// <summary>
/// Tests for <see cref="PromptBuilder.BuildIssueContextFileContent"/> image integration.
/// </summary>
public class PromptBuilderImageTests
{
    private static IssueDetail CreateIssue(string id = "42", string title = "Bug with UI",
        string description = "See screenshot:\n\n![error](https://github.com/user-attachments/assets/abc123.png)\n\nPlease fix.") => new()
    {
        Identifier = id,
        Title = title,
        Description = description,
        Labels = ["bug"]
    };

    private static ParsedIssue CreateParsedIssue() => new()
    {
        RequirementsSection = "Fix the bug",
        AcceptanceCriteria = ["Bug is fixed"]
    };

    private static List<DownloadedImage> CreateDownloadedImages() =>
    [
        new DownloadedImage
        {
            LocalPath = ".agent/images/issue-42-image-001.png",
            LocalFilename = "issue-42-image-001.png",
            Reference = new ImageReference
            {
                Url = "https://github.com/user-attachments/assets/abc123.png",
                AltText = "error",
                SourceType = ImageSourceType.Body,
                SourceIndex = 0
            },
            FileSizeBytes = 102400,
            MimeType = "image/png"
        }
    ];

    [Fact]
    public void WithImages_ReplacesInlineMarkdownImageUrl()
    {
        var issue = CreateIssue();
        var parsed = CreateParsedIssue();
        var images = CreateDownloadedImages();

        var result = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments: null, downloadedImages: images);

        result.Should().Contain("![error](.agent/images/issue-42-image-001.png)");
        result.Should().NotContain("![error](https://github.com/user-attachments/assets/abc123.png)");
    }

    [Fact]
    public void WithImages_DoesNotReplaceInsideFencedCodeBlock()
    {
        var description = "Text before\n\n```markdown\n![error](https://github.com/user-attachments/assets/abc123.png)\n```\n\nText after";
        var issue = CreateIssue(description: description);
        var parsed = CreateParsedIssue();
        var images = CreateDownloadedImages();

        var result = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments: null, downloadedImages: images);

        // Inside code block — must NOT be replaced
        result.Should().Contain("![error](https://github.com/user-attachments/assets/abc123.png)");
    }

    [Fact]
    public void WithImages_DoesNotReplaceInsideTildeFencedCodeBlock()
    {
        var description = "Text before\n\n~~~\n![error](https://github.com/user-attachments/assets/abc123.png)\n~~~\n\nText after";
        var issue = CreateIssue(description: description);
        var parsed = CreateParsedIssue();
        var images = CreateDownloadedImages();

        var result = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments: null, downloadedImages: images);

        result.Should().Contain("![error](https://github.com/user-attachments/assets/abc123.png)");
    }

    [Fact]
    public void WithImages_MixedFenceDelimiters_DoesNotCloseWithMismatchedDelimiter()
    {
        // ~~~ opens the block, ``` should NOT close it — only ~~~ can close it
        var description = "Text before\n\n~~~\n```\n![error](https://github.com/user-attachments/assets/abc123.png)\n~~~\n\nText after";
        var issue = CreateIssue(description: description);
        var parsed = CreateParsedIssue();
        var images = CreateDownloadedImages();

        var result = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments: null, downloadedImages: images);

        // The image URL is inside a ~~~-opened fence, so it must NOT be replaced
        result.Should().Contain("![error](https://github.com/user-attachments/assets/abc123.png)");
    }

    [Fact]
    public void WithImages_BacktickFenceNotClosedByTilde_DoesNotReplace()
    {
        // ``` opens the block, ~~~ should NOT close it — only ``` can close it
        var description = "Text before\n\n```\n~~~\n![error](https://github.com/user-attachments/assets/abc123.png)\n```\n\nText after";
        var issue = CreateIssue(description: description);
        var parsed = CreateParsedIssue();
        var images = CreateDownloadedImages();

        var result = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments: null, downloadedImages: images);

        // The image URL is inside a ```-opened fence, so it must NOT be replaced
        result.Should().Contain("![error](https://github.com/user-attachments/assets/abc123.png)");
    }

    [Fact]
    public void WithImages_HtmlImgGetsCommentBelow()
    {
        var description = "See this:\n\n<img src=\"https://github.com/user-attachments/assets/abc123.png\" alt=\"error\">\n\nDone.";
        var issue = CreateIssue(description: description);
        var parsed = CreateParsedIssue();
        var images = CreateDownloadedImages();

        var result = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments: null, downloadedImages: images);

        result.Should().Contain("<!-- Downloaded as .agent/images/issue-42-image-001.png -->");
    }

    [Fact]
    public void WithImages_AppendsAttachedImagesTable()
    {
        var issue = CreateIssue();
        var parsed = CreateParsedIssue();
        var images = CreateDownloadedImages();

        var result = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments: null, downloadedImages: images);

        result.Should().Contain("## Attached Images");
        result.Should().Contain("| # | File | Alt Text | Source |");
        result.Should().Contain("| 1 | issue-42-image-001.png | error | Body |");
    }

    [Fact]
    public void WithImages_MultipleImages_AllReplacedAndListed()
    {
        var description = "First: ![screenshot](https://example.com/img1.png)\n\nSecond: ![diagram](https://example.com/img2.jpg)";
        var issue = CreateIssue(description: description);
        var parsed = CreateParsedIssue();
        var images = new List<DownloadedImage>
        {
            new()
            {
                LocalPath = ".agent/images/issue-42-image-001.png",
                LocalFilename = "issue-42-image-001.png",
                Reference = new ImageReference
                {
                    Url = "https://example.com/img1.png",
                    AltText = "screenshot",
                    SourceType = ImageSourceType.Body,
                    SourceIndex = 0
                },
                FileSizeBytes = 50000,
                MimeType = "image/png"
            },
            new()
            {
                LocalPath = ".agent/images/issue-42-image-002.jpg",
                LocalFilename = "issue-42-image-002.jpg",
                Reference = new ImageReference
                {
                    Url = "https://example.com/img2.jpg",
                    AltText = "diagram",
                    SourceType = ImageSourceType.Comment,
                    SourceIndex = 3
                },
                FileSizeBytes = 80000,
                MimeType = "image/jpeg"
            }
        };

        var result = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments: null, downloadedImages: images);

        result.Should().Contain("![screenshot](.agent/images/issue-42-image-001.png)");
        result.Should().Contain("![diagram](.agent/images/issue-42-image-002.jpg)");
        result.Should().Contain("| 1 | issue-42-image-001.png | screenshot | Body |");
        result.Should().Contain("| 2 | issue-42-image-002.jpg | diagram | Comment #3 |");
    }

    [Fact]
    public void WithImages_NullDownloadedImages_BehavesLikeOriginal()
    {
        var issue = CreateIssue();
        var parsed = CreateParsedIssue();

        var result = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments: null, downloadedImages: null);

        // Original URL preserved, no attached images section
        result.Should().Contain("![error](https://github.com/user-attachments/assets/abc123.png)");
        result.Should().NotContain("## Attached Images");
    }

    [Fact]
    public void WithImages_EmptyDownloadedImages_BehavesLikeOriginal()
    {
        var issue = CreateIssue();
        var parsed = CreateParsedIssue();

        var result = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments: null, downloadedImages: []);

        result.Should().Contain("![error](https://github.com/user-attachments/assets/abc123.png)");
        result.Should().NotContain("## Attached Images");
    }

    [Fact]
    public void WithImages_DuplicateUrlInText_BothReplacedToSameLocalPath()
    {
        var description = "First: ![a](https://example.com/img.png)\n\nAgain: ![b](https://example.com/img.png)";
        var issue = CreateIssue(description: description);
        var parsed = CreateParsedIssue();
        var images = new List<DownloadedImage>
        {
            new()
            {
                LocalPath = ".agent/images/issue-42-image-001.png",
                LocalFilename = "issue-42-image-001.png",
                Reference = new ImageReference
                {
                    Url = "https://example.com/img.png",
                    AltText = "a",
                    SourceType = ImageSourceType.Body,
                    SourceIndex = 0
                },
                FileSizeBytes = 50000,
                MimeType = "image/png"
            }
        };

        var result = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments: null, downloadedImages: images);

        // Both occurrences replaced with same local path (alt text preserved per occurrence)
        result.Should().Contain("![a](.agent/images/issue-42-image-001.png)");
        result.Should().Contain("![b](.agent/images/issue-42-image-001.png)");
        result.Should().NotContain("https://example.com/img.png");
    }

    [Fact]
    public void WithImages_CommentSourceType_ShowsCommentInTable()
    {
        var issue = CreateIssue(description: "No images in body");
        var parsed = CreateParsedIssue();
        var images = new List<DownloadedImage>
        {
            new()
            {
                LocalPath = ".agent/images/issue-42-image-001.png",
                LocalFilename = "issue-42-image-001.png",
                Reference = new ImageReference
                {
                    Url = "https://example.com/comment-img.png",
                    AltText = "fix result",
                    SourceType = ImageSourceType.Comment,
                    SourceIndex = 5
                },
                FileSizeBytes = 30000,
                MimeType = "image/png"
            }
        };

        var result = PromptBuilder.BuildIssueContextFileContent(issue, parsed, comments: null, downloadedImages: images);

        result.Should().Contain("| 1 | issue-42-image-001.png | fix result | Comment #5 |");
    }

    #region Prompt Image Awareness Line

    [Fact]
    public void BuildAnalysisPrompt_WithImages_AppendsImageAwarenessLine()
    {
        var result = PromptBuilder.BuildAnalysisPrompt("Analyze", CreateIssue(), CreateParsedIssue(),
            imageCount: 3);

        result.Should().Contain("This issue includes 3 screenshot(s)/image(s) in `.agent/images/`");
    }

    [Fact]
    public void BuildAnalysisPrompt_WithZeroImages_OmitsImageAwarenessLine()
    {
        var result = PromptBuilder.BuildAnalysisPrompt("Analyze", CreateIssue(), CreateParsedIssue(),
            imageCount: 0);

        result.Should().NotContain("screenshot(s)/image(s)");
    }

    [Fact]
    public void BuildAnalysisPrompt_DefaultImageCount_OmitsImageAwarenessLine()
    {
        var result = PromptBuilder.BuildAnalysisPrompt("Analyze", CreateIssue(), CreateParsedIssue());

        result.Should().NotContain("screenshot(s)/image(s)");
    }

    [Fact]
    public void BuildPrompt_WithImages_AppendsImageAwarenessLine()
    {
        var result = PromptBuilder.BuildPrompt("Implement", CreateIssue(), CreateParsedIssue(),
            imageCount: 2);

        result.Should().Contain("This issue includes 2 screenshot(s)/image(s) in `.agent/images/`");
    }

    [Fact]
    public void BuildPrompt_WithZeroImages_OmitsImageAwarenessLine()
    {
        var result = PromptBuilder.BuildPrompt("Implement", CreateIssue(), CreateParsedIssue(),
            imageCount: 0);

        result.Should().NotContain("screenshot(s)/image(s)");
    }

    [Fact]
    public void BuildPrompt_DefaultImageCount_OmitsImageAwarenessLine()
    {
        var result = PromptBuilder.BuildPrompt("Implement", CreateIssue(), CreateParsedIssue());

        result.Should().NotContain("screenshot(s)/image(s)");
    }

    [Fact]
    public void BuildReviewPrompt_WithImages_AppendsImageAwarenessLine()
    {
        var findingsPath = ".agent/review-findings-test.md";
        var result = PromptBuilder.BuildReviewPrompt("Review", CreateIssue(), CreateParsedIssue(), findingsPath,
            imageCount: 5);

        result.Should().Contain("This issue includes 5 screenshot(s)/image(s) in `.agent/images/`");
    }

    [Fact]
    public void BuildReviewPrompt_WithZeroImages_OmitsImageAwarenessLine()
    {
        var findingsPath = ".agent/review-findings-test.md";
        var result = PromptBuilder.BuildReviewPrompt("Review", CreateIssue(), CreateParsedIssue(), findingsPath,
            imageCount: 0);

        result.Should().NotContain("screenshot(s)/image(s)");
    }

    [Fact]
    public void BuildReviewPrompt_DefaultImageCount_OmitsImageAwarenessLine()
    {
        var findingsPath = ".agent/review-findings-test.md";
        var result = PromptBuilder.BuildReviewPrompt("Review", CreateIssue(), CreateParsedIssue(), findingsPath);

        result.Should().NotContain("screenshot(s)/image(s)");
    }

    [Fact]
    public void BuildAnalysisPrompt_WithImages_ContainsExamineInstruction()
    {
        var result = PromptBuilder.BuildAnalysisPrompt("Analyze", CreateIssue(), CreateParsedIssue(),
            imageCount: 1);

        result.Should().Contain("examine them for visual context about the problem or expected behavior");
    }

    [Fact]
    public void BuildPrompt_WithImages_ContainsExamineInstruction()
    {
        var result = PromptBuilder.BuildPrompt("Implement", CreateIssue(), CreateParsedIssue(),
            imageCount: 1);

        result.Should().Contain("examine them for visual context about the problem or expected behavior");
    }

    [Fact]
    public void BuildReviewPrompt_WithImages_ContainsExamineInstruction()
    {
        var findingsPath = ".agent/review-findings-test.md";
        var result = PromptBuilder.BuildReviewPrompt("Review", CreateIssue(), CreateParsedIssue(), findingsPath,
            imageCount: 1);

        result.Should().Contain("examine them for visual context about the problem or expected behavior");
    }

    #endregion
}
