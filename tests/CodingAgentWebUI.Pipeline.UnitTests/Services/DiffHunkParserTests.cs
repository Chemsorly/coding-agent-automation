using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for DiffHunkParser — validates parsing of unified diff output
/// to extract valid line ranges per file for inline review comment validation.
/// Only lines with '+' prefix (added/modified) are valid targets; context lines are excluded.
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
    public void ParseValidLines_SingleFileWithSingleHunk_OnlyAddedLinesAreValid()
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
        // Only the two '+' lines should be valid (lines 11 and 12 in new file)
        // Line 10 = context (' unchanged line'), line 11 = '+added line 1',
        // line 12 = '+added line 2', line 13 = context (' unchanged line')
        validLines.Should().Contain(11);
        validLines.Should().Contain(12);
        validLines.Should().NotContain(10); // context line
        validLines.Should().NotContain(13); // context line
        validLines.Count.Should().Be(2);
    }

    // ─── 3. Single file with multiple hunks ─────────────────────────────────────

    [Fact]
    public void ParseValidLines_SingleFileWithMultipleHunks_OnlyAddedLinesFromBothHunks()
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

        // First hunk: line 1=context, line 2='+inserted', line 3=context, line 4=context
        validLines.Should().Contain(2); // '+inserted'
        validLines.Should().NotContain(1); // context

        // Second hunk: line 21=context, line 22='+new line 1', line 23='+new line 2', line 24=context, line 25=context
        validLines.Should().Contain(22); // '+new line 1'
        validLines.Should().Contain(23); // '+new line 2'
        validLines.Should().NotContain(21); // context

        // Total: 3 added lines
        validLines.Count.Should().Be(3);
    }

    // ─── 4. Multiple files ──────────────────────────────────────────────────────

    [Fact]
    public void ParseValidLines_MultipleFiles_EachFileHasOnlyAddedLines()
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

        // A.cs: line 1=context, line 2='+new' (replaces '-old'), line 3=context
        result["src/A.cs"].Should().Contain(2);
        result["src/A.cs"].Should().NotContain(1);
        result["src/A.cs"].Should().NotContain(3);
        result["src/A.cs"].Count.Should().Be(1);

        // B.cs: line 5=context, line 6='+added', line 7=context, line 8=context
        result["src/B.cs"].Should().Contain(6);
        result["src/B.cs"].Should().NotContain(5);
        result["src/B.cs"].Count.Should().Be(1);
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
        // All lines are '+' prefixed — all valid
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
    public void ParseValidLines_HunkHeaderWithoutSize_SingleAddedLine()
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
        // '+new line' is at line 1
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
        // line 1=context, line 2='+added line', line 3=context, line 4=context
        result["src/NewName.cs"].Should().Contain(2);
        result["src/NewName.cs"].Should().NotContain(1);
        result["src/NewName.cs"].Count.Should().Be(1);
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
    public void ParseValidLines_RealWorldDiff_OnlyAddedLinesAreValid()
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

        // First hunk (+95,25): 3 context lines (95-97), then 19 added lines (98-116), then 0 trailing context
        // Context: ' ' (line 95), '        var (comments...' (line 96), ' ' (line 97)
        // Added: lines 98-116 (the 19 '+' lines)
        validLines.Should().NotContain(95); // context (empty line)
        validLines.Should().NotContain(96); // context
        validLines.Should().NotContain(97); // context (empty line)
        validLines.Should().Contain(98);    // first '+' line
        validLines.Should().Contain(116);   // last '+' line

        // Second hunk (+169,7): 2 context lines, 1 added, 4 context
        // Line 169=context, 170=context, 171='+if(...)' , 172-175=context
        validLines.Should().NotContain(169); // context
        validLines.Should().Contain(171);    // the '+' replacement line
        validLines.Should().NotContain(172); // context

        // Only added lines should be present (19 from first hunk + 1 from second)
        validLines.Count.Should().Be(20);
    }

    // ─── 11. Context lines are excluded ─────────────────────────────────────────

    [Fact]
    public void ParseValidLines_ContextLinesExcluded_OnlyPlusLinesValid()
    {
        // A hunk with 3 context lines and 2 added lines
        var diff = """
            diff --git a/src/Mix.cs b/src/Mix.cs
            index abc1234..def5678 100644
            --- a/src/Mix.cs
            +++ b/src/Mix.cs
            @@ -1,4 +1,6 @@ namespace Mix
             context 1
             context 2
            +added 1
            +added 2
             context 3
             context 4
            """;

        var result = DiffHunkParser.ParseValidLines(diff);

        result.Should().ContainKey("src/Mix.cs");
        var validLines = result["src/Mix.cs"];

        // Lines: 1=context, 2=context, 3='+added 1', 4='+added 2', 5=context, 6=context
        validLines.Should().NotContain(1);
        validLines.Should().NotContain(2);
        validLines.Should().Contain(3);
        validLines.Should().Contain(4);
        validLines.Should().NotContain(5);
        validLines.Should().NotContain(6);
        validLines.Count.Should().Be(2);
    }

    // ─── 12. Deleted lines don't advance counter ────────────────────────────────

    [Fact]
    public void ParseValidLines_DeletedLinesDontAdvanceCounter()
    {
        // A replacement: 2 deleted lines followed by 1 added line
        var diff = """
            diff --git a/src/Replace.cs b/src/Replace.cs
            index abc1234..def5678 100644
            --- a/src/Replace.cs
            +++ b/src/Replace.cs
            @@ -5,4 +5,3 @@ namespace Replace
             context before
            -old line 1
            -old line 2
            +replacement line
             context after
            """;

        var result = DiffHunkParser.ParseValidLines(diff);

        result.Should().ContainKey("src/Replace.cs");
        var validLines = result["src/Replace.cs"];

        // Line 5=context, '-old line 1' (no new line advance), '-old line 2' (no advance),
        // line 6='+replacement line', line 7=context
        validLines.Should().NotContain(5); // context
        validLines.Should().Contain(6);    // the '+' replacement
        validLines.Should().NotContain(7); // context
        validLines.Count.Should().Be(1);
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
        // Only the '+new' line (line 2) should be valid
        result["src/path/File.cs"].Should().Contain(2);
        result["src/path/File.cs"].Should().NotContain(1);
        result["src/path/File.cs"].Should().NotContain(3);
        result["src/path/File.cs"].Count.Should().Be(1);
    }
}
