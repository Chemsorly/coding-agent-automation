using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for DiffHunkParser — validates parsing of unified diff output
/// to extract valid line ranges per file for inline review comment validation.
/// </summary>
[Trait("Feature", "026-inline-review-comments")]
public class DiffHunkParserTests
{
    // ─── 1. Null/empty input ────────────────────────────────────────────────────

    [Fact]
    public void ParseValidLines_NullInput_ReturnsEmptyDictionary()
    {
        var result = DiffHunkParser.ParseValidLines(null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseValidLines_EmptyString_ReturnsEmptyDictionary()
    {
        var result = DiffHunkParser.ParseValidLines(string.Empty);

        result.Should().BeEmpty();
    }

    // ─── 2. Single file with single hunk ────────────────────────────────────────

    [Fact]
    public void ParseValidLines_SingleFileWithSingleHunk_ExtractsCorrectLineRange()
    {
        var diff = """
            diff --git a/src/Foo.cs b/src/Foo.cs
            index abc1234..def5678 100644
            --- a/src/Foo.cs
            +++ b/src/Foo.cs
            @@ -10,5 +10,7 @@ namespace Foo
             unchanged line
            +added line 1
            +added line 2
             unchanged line
            """;

        var result = DiffHunkParser.ParseValidLines(diff);

        result.Should().ContainKey("src/Foo.cs");
        var validLines = result["src/Foo.cs"];
        // Hunk: +10,7 → lines 10 through 16
        validLines.Should().Contain(10);
        validLines.Should().Contain(16);
        validLines.Should().NotContain(9);
        validLines.Should().NotContain(17);
        validLines.Count.Should().Be(7);
    }

    // ─── 3. Single file with multiple hunks ─────────────────────────────────────

    [Fact]
    public void ParseValidLines_SingleFileWithMultipleHunks_UnionsAllRanges()
    {
        var diff = """
            diff --git a/src/Bar.cs b/src/Bar.cs
            index abc1234..def5678 100644
            --- a/src/Bar.cs
            +++ b/src/Bar.cs
            @@ -1,3 +1,4 @@ namespace Bar
             line 1
            +inserted
             line 2
             line 3
            @@ -20,3 +21,5 @@ class Bar
             line 20
            +new line 1
            +new line 2
             line 22
            """;

        var result = DiffHunkParser.ParseValidLines(diff);

        result.Should().ContainKey("src/Bar.cs");
        var validLines = result["src/Bar.cs"];

        // First hunk: +1,4 → lines 1-4
        validLines.Should().Contain(1);
        validLines.Should().Contain(4);

        // Second hunk: +21,5 → lines 21-25
        validLines.Should().Contain(21);
        validLines.Should().Contain(25);

        // Gap between hunks should not be valid
        validLines.Should().NotContain(5);
        validLines.Should().NotContain(20);
    }

    // ─── 4. Multiple files ──────────────────────────────────────────────────────

    [Fact]
    public void ParseValidLines_MultipleFiles_EachFileHasOwnValidLines()
    {
        var diff = """
            diff --git a/src/A.cs b/src/A.cs
            index abc1234..def5678 100644
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -1,3 +1,3 @@ namespace A
             line 1
            -old
            +new
             line 3
            diff --git a/src/B.cs b/src/B.cs
            index abc1234..def5678 100644
            --- a/src/B.cs
            +++ b/src/B.cs
            @@ -5,3 +5,4 @@ namespace B
             line 5
            +added
             line 6
             line 7
            """;

        var result = DiffHunkParser.ParseValidLines(diff);

        result.Should().HaveCount(2);
        result.Should().ContainKey("src/A.cs");
        result.Should().ContainKey("src/B.cs");

        // A.cs: +1,3 → lines 1-3
        result["src/A.cs"].Should().Contain(1);
        result["src/A.cs"].Should().Contain(3);
        result["src/A.cs"].Count.Should().Be(3);

        // B.cs: +5,4 → lines 5-8
        result["src/B.cs"].Should().Contain(5);
        result["src/B.cs"].Should().Contain(8);
        result["src/B.cs"].Count.Should().Be(4);
    }

    // ─── 5. New file (all added) ────────────────────────────────────────────────

    [Fact]
    public void ParseValidLines_NewFile_AllLinesValid()
    {
        var diff = """
            diff --git a/src/New.cs b/src/New.cs
            new file mode 100644
            index 0000000..abc1234
            --- /dev/null
            +++ b/src/New.cs
            @@ -0,0 +1,5 @@
            +line 1
            +line 2
            +line 3
            +line 4
            +line 5
            """;

        var result = DiffHunkParser.ParseValidLines(diff);

        result.Should().ContainKey("src/New.cs");
        var validLines = result["src/New.cs"];
        // +1,5 → lines 1-5
        validLines.Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5 });
    }

    // ─── 6. Deleted file ────────────────────────────────────────────────────────

    [Fact]
    public void ParseValidLines_DeletedFile_NoValidLines()
    {
        var diff = """
            diff --git a/src/Old.cs b/src/Old.cs
            deleted file mode 100644
            index abc1234..0000000
            --- a/src/Old.cs
            +++ /dev/null
            @@ -1,5 +0,0 @@
            -line 1
            -line 2
            -line 3
            -line 4
            -line 5
            """;

        var result = DiffHunkParser.ParseValidLines(diff);

        // Deleted file should not appear in the result (no valid RIGHT-side lines)
        result.Should().NotContainKey("src/Old.cs");
    }

    // ─── 7. Hunk header without size (defaults to 1) ────────────────────────────

    [Fact]
    public void ParseValidLines_HunkHeaderWithoutSize_DefaultsToOne()
    {
        var diff = """
            diff --git a/src/Single.cs b/src/Single.cs
            index abc1234..def5678 100644
            --- a/src/Single.cs
            +++ b/src/Single.cs
            @@ -1 +1 @@
            -old line
            +new line
            """;

        var result = DiffHunkParser.ParseValidLines(diff);

        result.Should().ContainKey("src/Single.cs");
        var validLines = result["src/Single.cs"];
        // +1 (no size) → size defaults to 1 → only line 1
        validLines.Should().BeEquivalentTo(new[] { 1 });
    }

    // ─── 8. Renamed file ────────────────────────────────────────────────────────

    [Fact]
    public void ParseValidLines_RenamedFile_UsesNewPath()
    {
        var diff = """
            diff --git a/src/OldName.cs b/src/NewName.cs
            similarity index 90%
            rename from src/OldName.cs
            rename to src/NewName.cs
            index abc1234..def5678 100644
            --- a/src/OldName.cs
            +++ b/src/NewName.cs
            @@ -1,3 +1,4 @@ namespace Renamed
             line 1
            +added line
             line 2
             line 3
            """;

        var result = DiffHunkParser.ParseValidLines(diff);

        // Should use the new path from +++ b/src/NewName.cs
        result.Should().ContainKey("src/NewName.cs");
        result.Should().NotContainKey("src/OldName.cs");
        result["src/NewName.cs"].Should().Contain(1);
        result["src/NewName.cs"].Should().Contain(4);
        result["src/NewName.cs"].Count.Should().Be(4);
    }

    // ─── 9. Binary file ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseValidLines_BinaryFile_NoValidLines()
    {
        var diff = """
            diff --git a/assets/image.png b/assets/image.png
            index abc1234..def5678 100644
            Binary files a/assets/image.png and b/assets/image.png differ
            """;

        var result = DiffHunkParser.ParseValidLines(diff);

        // Binary files have no hunk headers, so no valid lines
        // The file won't even appear in the result because there's no +++ line
        result.Should().NotContainKey("assets/image.png");
    }

    // ─── 10. Real-world diff snippet ────────────────────────────────────────────

    [Fact]
    public void ParseValidLines_RealWorldDiff_ValidatesCorrectly()
    {
        var diff = """
            diff --git a/src/CodingAgentWebUI.Pipeline/Services/Steps/PostReviewFindingsStep.cs b/src/CodingAgentWebUI.Pipeline/Services/Steps/PostReviewFindingsStep.cs
            index 1a2b3c4..5d6e7f8 100644
            --- a/src/CodingAgentWebUI.Pipeline/Services/Steps/PostReviewFindingsStep.cs
            +++ b/src/CodingAgentWebUI.Pipeline/Services/Steps/PostReviewFindingsStep.cs
            @@ -95,6 +95,25 @@ internal sealed class PostReviewFindingsStep : IPipelineStep
             
                     var (comments, excludedCount) = FindingsSelector.Select(findingsWithLocation, inlineSettings);
             
            +        // Step 6.5: Filter comments to only those targeting lines within diff hunks.
            +        var diffPath = Path.Combine(context.Run.WorkspacePath!, AgentWorkspacePaths.FullDiffFilePath);
            +        IReadOnlyList<ReviewComment> validComments = comments;
            +        if (File.Exists(diffPath))
            +        {
            +            try
            +            {
            +                var diffText = await File.ReadAllTextAsync(diffPath, ct);
            +                var validLines = DiffHunkParser.ParseValidLines(diffText);
            +                validComments = comments
            +                    .Where(c => validLines.TryGetValue(c.Path, out var lines) && lines.Contains(c.Line))
            +                    .ToList();
            +            }
            +            catch (Exception ex) when (ex is not OperationCanceledException)
            +            {
            +                context.Logger.Warning(ex, "Failed to parse diff for hunk validation");
            +            }
            +        }
            +
                     // Step 7: Build ReviewSubmission with CommitId
                     string? commitId = null;
            @@ -150,7 +169,7 @@ internal sealed class PostReviewFindingsStep : IPipelineStep
                 private static PullRequestReviewType DetermineReviewType(PipelineRun run)
                 {
            -        if (run.CodeReviewCriticalCount > 0)
            +        if (run.CodeReviewCriticalCount > 0 || run.CodeReviewWarningCount > 0)
                         return PullRequestReviewType.RequestChanges;
             
                     if (run.CodeReviewSuggestionCount > 0)
            """;

        var result = DiffHunkParser.ParseValidLines(diff);

        var filePath = "src/CodingAgentWebUI.Pipeline/Services/Steps/PostReviewFindingsStep.cs";
        result.Should().ContainKey(filePath);

        var validLines = result[filePath];

        // First hunk: +95,25 → lines 95-119
        validLines.Should().Contain(95);
        validLines.Should().Contain(119);
        validLines.Should().NotContain(94);
        validLines.Should().NotContain(120);

        // Second hunk: +169,7 → lines 169-175
        validLines.Should().Contain(169);
        validLines.Should().Contain(175);
        validLines.Should().NotContain(168);
        validLines.Should().NotContain(176);
    }

    // ─── Edge case: path normalization ──────────────────────────────────────────

    [Fact]
    public void ParseValidLines_PathsAreNormalized_ForwardSlashes()
    {
        var diff = "diff --git a/src\\path\\File.cs b/src\\path\\File.cs\n" +
                   "--- a/src\\path\\File.cs\n" +
                   "+++ b/src\\path\\File.cs\n" +
                   "@@ -1,3 +1,3 @@\n" +
                   " line 1\n" +
                   "-old\n" +
                   "+new\n" +
                   " line 3\n";

        var result = DiffHunkParser.ParseValidLines(diff);

        // Backslashes in the path should be normalized to forward slashes
        result.Should().ContainKey("src/path/File.cs");
    }
}
