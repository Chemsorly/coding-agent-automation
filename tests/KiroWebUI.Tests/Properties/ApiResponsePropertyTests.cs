using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using KiroCliLib.Models;
using KiroWebUI.Models;
using Xunit;
using TestResult = KiroCliLib.Models.TestResult;

namespace KiroWebUI.Tests.Properties;

/// <summary>
/// Property 9: API Response Completeness
/// For any execution result (exit code, output lines, optional file changes, optional test results),
/// the PromptResponse SHALL contain the exit code, all output lines in order, and the file changes
/// and test results when present.
/// Feature: kiro-web-ui
/// Validates: Requirements 4.4
/// </summary>
public class ApiResponsePropertyTests
{
    public static class Generators
    {
        public static Arbitrary<PromptResponseTestCase> PromptResponseTestCase()
        {
            var exitCodeGen = Gen.Choose(-1, 255);

            var outputLineGen = Gen.Elements("Analyzing workspace...", "Found 5 files", "Creating plan",
                "Implementing feature", "Running tests", "✓ Task completed", "Done", "Writing to src/file.cs");

            var outputLinesGen = Gen.Choose(0, 10).SelectMany(count =>
                outputLineGen.ArrayOf(count).Select(arr => arr.ToList()));

            var fileChangeGen = Gen.Elements(
                new FileChange { Path = "src/Program.cs", Type = FileChangeType.Created },
                new FileChange { Path = "tests/Test.cs", Type = FileChangeType.Modified },
                new FileChange { Path = "old/Legacy.cs", Type = FileChangeType.Deleted },
                new FileChange { Path = "config/app.json", Type = FileChangeType.Modified });

            var fileChangesGen = Gen.OneOf(
                Gen.Constant<IReadOnlyList<FileChange>?>(null),
                Gen.Choose(1, 5).SelectMany(count =>
                    fileChangeGen.ArrayOf(count)
                        .Select(arr => (IReadOnlyList<FileChange>?)arr.ToList().AsReadOnly())));

            var testResultGen = Gen.OneOf(
                Gen.Constant<TestResult?>(null),
                Gen.Choose(1, 50).SelectMany(total =>
                    Gen.Choose(0, total).Select(passed =>
                        (TestResult?)new TestResult
                        {
                            TotalTests = total,
                            PassedTests = passed,
                            FailedTests = total - passed
                        })));

            var gen = from exitCode in exitCodeGen
                      from lines in outputLinesGen
                      from fc in fileChangesGen
                      from tr in testResultGen
                      select new PromptResponseTestCase
                      {
                          ExitCode = exitCode,
                          OutputLines = lines,
                          FileChanges = fc,
                          TestResults = tr
                      };

            return gen.ToArbitrary();
        }
    }

    public class PromptResponseTestCase
    {
        public required int ExitCode { get; init; }
        public required List<string> OutputLines { get; init; }
        public IReadOnlyList<FileChange>? FileChanges { get; init; }
        public TestResult? TestResults { get; init; }
    }

    /// <summary>
    /// Property 9: PromptResponse serialization round-trip preserves all fields —
    /// exit code, output lines in order, file changes, and test results.
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Generators) })]
    public bool PromptResponse_RoundTrip_PreservesAllFields(PromptResponseTestCase testCase)
    {
        var original = new PromptResponse
        {
            ExitCode = testCase.ExitCode,
            OutputLines = testCase.OutputLines.AsReadOnly(),
            FileChanges = testCase.FileChanges,
            TestResults = testCase.TestResults
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<PromptResponseDeserialized>(json);

        if (deserialized == null) return false;

        // Exit code preserved
        if (deserialized.ExitCode != original.ExitCode) return false;

        // Output lines preserved in order
        if (deserialized.OutputLines == null) return false;
        if (deserialized.OutputLines.Count != original.OutputLines.Count) return false;
        for (int i = 0; i < original.OutputLines.Count; i++)
        {
            if (deserialized.OutputLines[i] != original.OutputLines[i]) return false;
        }

        // File changes: null when null, present when present
        if (original.FileChanges == null)
        {
            if (deserialized.FileChanges != null) return false;
        }
        else
        {
            if (deserialized.FileChanges == null) return false;
            if (deserialized.FileChanges.Count != original.FileChanges.Count) return false;
            for (int i = 0; i < original.FileChanges.Count; i++)
            {
                if (deserialized.FileChanges[i].Path != original.FileChanges[i].Path) return false;
            }
        }

        // Test results: null when null, present when present
        if (original.TestResults == null)
        {
            if (deserialized.TestResults != null) return false;
        }
        else
        {
            if (deserialized.TestResults == null) return false;
            if (deserialized.TestResults.TotalTests != original.TestResults.TotalTests) return false;
            if (deserialized.TestResults.PassedTests != original.TestResults.PassedTests) return false;
            if (deserialized.TestResults.FailedTests != original.TestResults.FailedTests) return false;
        }

        return true;
    }

    /// <summary>
    /// Deserialization-friendly version of PromptResponse (no 'required' constraint).
    /// </summary>
    private class PromptResponseDeserialized
    {
        public int ExitCode { get; set; }
        public List<string>? OutputLines { get; set; }
        public List<FileChangeDeserialized>? FileChanges { get; set; }
        public TestResultDeserialized? TestResults { get; set; }
    }

    private class FileChangeDeserialized
    {
        public string Path { get; set; } = string.Empty;
        public int Type { get; set; }
    }

    private class TestResultDeserialized
    {
        public int TotalTests { get; set; }
        public int PassedTests { get; set; }
        public int FailedTests { get; set; }
    }
}
