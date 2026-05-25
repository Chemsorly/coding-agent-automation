using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for DiffHunkParser: for any generated diff with known hunk ranges,
/// ParseValidLines returns exactly the expected line numbers for each file.
/// Feature: 026-inline-review-comments
/// </summary>
[Trait("Feature", "026-inline-review-comments")]
public class DiffHunkParserPropertyTests
{
    private static readonly string[] FileExtensions = [".cs", ".ts", ".py", ".java", ".go", ".rs"];
    private static readonly string[] DirectorySegments = ["src", "lib", "tests", "services", "models", "utils"];

    /// <summary>
    /// Property: For any generated diff with known hunk ranges, ParseValidLines returns
    /// exactly the expected line numbers for each file. The generated diff has deterministic
    /// hunk headers, so we can verify the parser output against the expected ranges.
    /// Handles duplicate file paths by unioning all hunks for the same path.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property ParseValidLines_ReturnsExactlyExpectedLineNumbers()
    {
        var gen =
            from fileCount in Gen.Choose(1, 5)
            from files in Gen.ArrayOf(GenFileWithHunks(), fileCount)
            select files;

        return Prop.ForAll(gen.ToArbitrary(), files =>
        {
            // Build a synthetic diff from the generated file/hunk data
            var diffText = BuildDiff(files);

            // Parse it
            var result = DiffHunkParser.ParseValidLines(diffText);

            // Build expected lines per file (union all hunks for duplicate paths)
            var expectedByPath = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
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
                    for (var i = hunk.NewStart; i < hunk.NewStart + hunk.NewSize; i++)
                        expectedLines.Add(i);
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

            // Verify each expected file's valid lines match
            foreach (var (path, expectedLines) in expectedByPath)
            {
                result.Should().ContainKey(path,
                    $"file '{path}' should appear in result");

                result[path].Should().BeEquivalentTo(expectedLines,
                    $"valid lines for '{path}' should match expected hunk ranges");
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
                return; // File not in result (e.g., no hunks) — trivially true

            var validLines = result[file.Path];

            // Find the maximum valid line and probe beyond it
            var maxLine = file.Hunks.Max(h => h.NewStart + h.NewSize - 1);
            var probeLine = maxLine + probeOffset;

            validLines.Should().NotContain(probeLine,
                $"line {probeLine} is beyond all hunk ranges and should not be valid");
        });
    }

    /// <summary>
    /// Property: The number of valid lines for a file equals the sum of all hunk sizes.
    /// (Assuming non-overlapping hunks, which our generator guarantees.)
    /// </summary>
    [Property(MaxTest = 200)]
    public Property ParseValidLines_ValidLineCount_EqualsSumOfHunkSizes()
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

            var expectedCount = file.Hunks.Sum(h => h.NewSize);
            result[file.Path].Count.Should().Be(expectedCount,
                $"valid line count should equal sum of hunk sizes ({expectedCount})");
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
        return Gen.Choose(1, 50).Select(startOffset =>
        {
            var hunks = new GeneratedHunk[count];
            var currentStart = startOffset;

            for (var i = 0; i < count; i++)
            {
                var size = Random.Shared.Next(1, 15);
                var oldStart = Math.Max(1, currentStart - Random.Shared.Next(0, 3));
                var oldSize = Random.Shared.Next(1, 10);
                hunks[i] = new GeneratedHunk(oldStart, oldSize, currentStart, size);
                currentStart += size + Random.Shared.Next(5, 30); // Gap between hunks
            }

            return hunks;
        });
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
                    lines.Add($"@@ -{hunk.OldStart},{hunk.OldSize} +{hunk.NewStart},{hunk.NewSize} @@ context");

                    // Add some plausible diff content lines
                    for (var i = 0; i < hunk.NewSize; i++)
                    {
                        lines.Add($"+added line {hunk.NewStart + i}");
                    }
                }
            }
        }

        return string.Join("\n", lines);
    }

    // ─── Test Data Types ────────────────────────────────────────────────────────

    private sealed record GeneratedFile(string Path, GeneratedHunk[] Hunks, bool IsDeleted);
    private sealed record GeneratedHunk(int OldStart, int OldSize, int NewStart, int NewSize);
}
