using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for DiffHunkParser: for any generated diff with known hunk content,
/// ParseValidLines returns exactly the added ('+') line numbers for each file.
/// Context lines (' ') and deleted lines ('-') are never included.
/// Feature: 026-inline-review-comments
/// </summary>
[Trait("Feature", "026-inline-review-comments")]
public class DiffHunkParserPropertyTests
{
    private static readonly string[] FileExtensions = [".cs", ".ts", ".py", ".java", ".go", ".rs"];
    private static readonly string[] DirectorySegments = ["src", "lib", "tests", "services", "models", "utils"];

    /// <summary>
    /// Property: For any generated diff with known added lines, ParseValidLines returns
    /// exactly those line numbers. Context and deleted lines are excluded.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property ParseValidLines_ReturnsOnlyAddedLineNumbers()
    {
        var gen =
            from fileCount in Gen.Choose(1, 5)
            from files in Gen.ArrayOf(GenFileWithHunks(), fileCount)
            select files;

        return Prop.ForAll(gen.ToArbitrary(), files =>
        {
            var diffText = BuildDiff(files);
            var result = DiffHunkParser.ParseValidLines(diffText);

            // Build expected lines per file — only lines marked as Added
            var expectedByPath = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (file.IsDeleted)
                    continue;

                if (!expectedByPath.TryGetValue(file.Path, out var expectedLines))
                {
                    expectedLines = new HashSet<int>();
                    expectedByPath[file.Path] = expectedLines;
                }

                foreach (var hunk in file.Hunks)
                {
                    var newLine = hunk.NewStart;
                    foreach (var entry in hunk.Lines)
                    {
                        switch (entry)
                        {
                            case DiffLineType.Added:
                                expectedLines.Add(newLine);
                                newLine++;
                                break;
                            case DiffLineType.Context:
                                newLine++;
                                break;
                            case DiffLineType.Deleted:
                                // Doesn't advance new-line counter
                                break;
                        }
                    }
                }
            }

            // Verify deleted files are not in result
            foreach (var file in files.Where(f => f.IsDeleted))
            {
                if (!expectedByPath.ContainsKey(file.Path))
                {
                    result.Should().NotContainKey(file.Path,
                        $"deleted file '{file.Path}' should not appear in result");
                }
            }

            // Verify each expected file's valid lines match exactly
            foreach (var (path, expectedLines) in expectedByPath)
            {
                result.Should().ContainKey(path,
                    $"file '{path}' should appear in result");

                result[path].Should().BeEquivalentTo(expectedLines,
                    $"valid lines for '{path}' should match only added lines");
            }
        });
    }

    /// <summary>
    /// Property: Lines outside all hunk ranges for a file are never included in the valid set.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property ParseValidLines_LinesOutsideHunks_NeverIncluded()
    {
        var gen =
            from file in GenFileWithHunks()
            where !file.IsDeleted && file.Hunks.Length > 0
            from probeOffset in Gen.Choose(1, 100)
            select (file, probeOffset);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (file, probeOffset) = tuple;

            var diffText = BuildDiff([file]);
            var result = DiffHunkParser.ParseValidLines(diffText);

            if (!result.ContainsKey(file.Path))
                return; // File not in result — trivially true

            var validLines = result[file.Path];

            // Find the maximum new-line number across all hunks and probe beyond it
            var maxLine = 0;
            foreach (var hunk in file.Hunks)
            {
                var newLine = hunk.NewStart;
                foreach (var entry in hunk.Lines)
                {
                    if (entry != DiffLineType.Deleted)
                        newLine++;
                }
                maxLine = Math.Max(maxLine, newLine);
            }

            var probeLine = maxLine + probeOffset;
            validLines.Should().NotContain(probeLine,
                $"line {probeLine} is beyond all hunk ranges and should not be valid");
        });
    }

    /// <summary>
    /// Property: Context lines within hunks are never included in the valid set.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property ParseValidLines_ContextLines_NeverIncluded()
    {
        var gen =
            from file in GenFileWithHunks()
            where !file.IsDeleted && file.Hunks.Length > 0
                  && file.Hunks.Any(h => h.Lines.Contains(DiffLineType.Context))
            select file;

        return Prop.ForAll(gen.ToArbitrary(), file =>
        {
            var diffText = BuildDiff([file]);
            var result = DiffHunkParser.ParseValidLines(diffText);

            if (!result.ContainsKey(file.Path))
                return;

            var validLines = result[file.Path];

            // Collect all context line numbers
            foreach (var hunk in file.Hunks)
            {
                var newLine = hunk.NewStart;
                foreach (var entry in hunk.Lines)
                {
                    if (entry == DiffLineType.Context)
                    {
                        validLines.Should().NotContain(newLine,
                            $"context line {newLine} should not be in valid set");
                        newLine++;
                    }
                    else if (entry == DiffLineType.Added)
                    {
                        newLine++;
                    }
                    // Deleted lines don't advance counter
                }
            }
        });
    }

    /// <summary>
    /// Property: The number of valid lines equals the number of Added entries across all hunks.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property ParseValidLines_ValidLineCount_EqualsAddedLineCount()
    {
        var gen =
            from file in GenFileWithHunks()
            where !file.IsDeleted && file.Hunks.Length > 0
            select file;

        return Prop.ForAll(gen.ToArbitrary(), file =>
        {
            var diffText = BuildDiff([file]);
            var result = DiffHunkParser.ParseValidLines(diffText);

            if (!result.ContainsKey(file.Path))
                return;

            var expectedCount = file.Hunks.Sum(h => h.Lines.Count(l => l == DiffLineType.Added));
            result[file.Path].Count.Should().Be(expectedCount,
                $"valid line count should equal number of added lines ({expectedCount})");
        });
    }

    // ─── Generators ─────────────────────────────────────────────────────────────

    private static Gen<GeneratedFile> GenFileWithHunks()
    {
        return
            from path in GenFilePath()
            from isDeleted in Gen.Frequency((1, Gen.Constant(true)), (9, Gen.Constant(false)))
            from hunkCount in Gen.Choose(1, 4)
            from hunks in GenNonOverlappingHunks(hunkCount)
            select new GeneratedFile(path, isDeleted ? Array.Empty<GeneratedHunk>() : hunks, isDeleted);
    }

    private static Gen<string> GenFilePath()
    {
        return
            from segCount in Gen.Choose(1, 3)
            from segments in Gen.ArrayOf(Gen.Elements(DirectorySegments), segCount)
            from name in Gen.Elements("File", "Service", "Controller", "Model", "Helper", "Utils")
            from ext in Gen.Elements(FileExtensions)
            select string.Join("/", segments) + "/" + name + ext;
    }

    private static Gen<GeneratedHunk[]> GenNonOverlappingHunks(int count)
    {
        return
            from startOffset in Gen.Choose(1, 50)
            from hunkDefs in Gen.ArrayOf(GenHunkDefinition(), count)
            from gaps in Gen.ArrayOf(Gen.Choose(5, 29), count)
            select BuildHunks(startOffset, hunkDefs, gaps, count);
    }

    private static Gen<HunkDefinition> GenHunkDefinition()
    {
        // Generate a mix of context, added, and deleted lines
        return
            from lineCount in Gen.Choose(1, 10)
            from lines in Gen.ArrayOf(Gen.Elements(DiffLineType.Context, DiffLineType.Added, DiffLineType.Deleted), lineCount)
            // Ensure at least one added line so the hunk is meaningful
            from extraAdded in Gen.Choose(1, 3)
            from oldSize in Gen.Choose(1, 5)
            select new HunkDefinition(EnsureAtLeastOneAdded(lines, extraAdded), oldSize);
    }

    private static DiffLineType[] EnsureAtLeastOneAdded(DiffLineType[] lines, int extraAdded)
    {
        if (lines.Any(l => l == DiffLineType.Added))
            return lines;

        // Append added lines to ensure the hunk has at least one
        return lines.Concat(Enumerable.Repeat(DiffLineType.Added, extraAdded)).ToArray();
    }

    private static GeneratedHunk[] BuildHunks(int startOffset, HunkDefinition[] defs, int[] gaps, int count)
    {
        var hunks = new GeneratedHunk[count];
        var currentNewStart = startOffset;

        for (var i = 0; i < count; i++)
        {
            var def = defs[i];
            var oldStart = Math.Max(1, currentNewStart - 1);

            // Compute new size (lines that advance the new-line counter)
            var newSize = def.Lines.Count(l => l != DiffLineType.Deleted);

            hunks[i] = new GeneratedHunk(oldStart, def.OldSize, currentNewStart, newSize, def.Lines);
            currentNewStart += newSize + gaps[i] + 5; // Gap between hunks
        }

        return hunks;
    }

    // ─── Diff Builder ───────────────────────────────────────────────────────────

    private static string BuildDiff(GeneratedFile[] files)
    {
        var lines = new List<string>();

        foreach (var file in files)
        {
            lines.Add($"diff --git a/{file.Path} b/{file.Path}");
            lines.Add("index abc1234..def5678 100644");

            if (file.IsDeleted)
            {
                lines.Add($"--- a/{file.Path}");
                lines.Add("+++ /dev/null");
                lines.Add("@@ -1,5 +0,0 @@");
                lines.Add("-deleted line");
            }
            else
            {
                lines.Add($"--- a/{file.Path}");
                lines.Add($"+++ b/{file.Path}");

                foreach (var hunk in file.Hunks)
                {
                    var newSize = hunk.Lines.Count(l => l != DiffLineType.Deleted);
                    lines.Add($"@@ -{hunk.OldStart},{hunk.OldSize} +{hunk.NewStart},{newSize} @@ context");

                    // Generate realistic diff content based on line types
                    var lineNum = hunk.NewStart;
                    foreach (var lineType in hunk.Lines)
                    {
                        switch (lineType)
                        {
                            case DiffLineType.Added:
                                lines.Add($"+added line {lineNum}");
                                lineNum++;
                                break;
                            case DiffLineType.Context:
                                lines.Add($" context line {lineNum}");
                                lineNum++;
                                break;
                            case DiffLineType.Deleted:
                                lines.Add($"-deleted line");
                                break;
                        }
                    }
                }
            }
        }

        return string.Join("\n", lines);
    }

    // ─── Test Data Types ────────────────────────────────────────────────────────

    private enum DiffLineType { Context, Added, Deleted }
    private sealed record GeneratedFile(string Path, GeneratedHunk[] Hunks, bool IsDeleted);
    private sealed record GeneratedHunk(int OldStart, int OldSize, int NewStart, int NewSize, DiffLineType[] Lines);
    private sealed record HunkDefinition(DiffLineType[] Lines, int OldSize);
}
