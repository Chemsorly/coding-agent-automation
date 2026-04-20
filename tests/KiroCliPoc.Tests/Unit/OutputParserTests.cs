using FsCheck;
using FsCheck.Xunit;
using FsCheck.Fluent;
using Xunit;
using TestResult = KiroCliLib.Models.TestResult;

namespace KiroCliPoc.Tests.Unit;

/// <summary>
/// Property-based tests for OutputParser.
/// Feature: kiro-cli-poc
/// </summary>
public class OutputParserTests
{
    /// <summary>
    /// Property 2: State Detection Accuracy
    /// For any Kiro CLI output containing state markers, the Output Parser should correctly identify the state.
    /// Validates: Requirements 3.1, 3.2, 3.3, 3.4
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(Generators) })]
    public bool StateDetection_ShouldIdentifyCorrectState(StateMarkerTestCase testCase)
    {
        // Arrange
        var parser = new KiroCliLib.Core.OutputParser();
        KiroCliLib.Models.KiroState? detectedState = null;
        parser.StateChanged += (sender, state) => detectedState = state;

        // Act
        parser.ProcessLine(testCase.Line);

        // Assert
        var result = detectedState == testCase.ExpectedState;
        if (!result)
        {
            throw new Exception($"State detection failed for line: '{testCase.Line}'. Expected: {testCase.ExpectedState}, Got: {detectedState}");
        }
        return result;
    }

    /// <summary>
    /// Property 3: Data Extraction Correctness (File Operations)
    /// For any Kiro CLI output containing file paths, the Output Parser should correctly extract the data.
    /// Validates: Requirements 3.5
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(Generators) })]
    public bool FileDetection_ShouldExtractFilePath(FileOperationTestCase testCase)
    {
        // Arrange
        var parser = new KiroCliLib.Core.OutputParser();
        KiroCliLib.Models.FileChange? detectedFile = null;
        parser.FileDetected += (sender, file) => detectedFile = file;

        // Act
        parser.ProcessLine(testCase.Line);

        // Assert
        var pathMatches = detectedFile != null && detectedFile.Path == testCase.ExpectedPath;
        var typeMatches = detectedFile != null && detectedFile.Type == testCase.ExpectedType;

        return pathMatches && typeMatches;
    }

    /// <summary>
    /// Property 3: Data Extraction Correctness (Test Results)
    /// For any Kiro CLI output containing test results, the Output Parser should correctly extract the data.
    /// Validates: Requirements 3.6
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(Generators) })]
    public bool TestResultDetection_ShouldExtractCounts(TestResultTestCase testCase)
    {
        // Arrange
        var parser = new KiroCliLib.Core.OutputParser();
        TestResult? detectedResult = null;
        parser.TestResultDetected += (sender, result) => detectedResult = result;

        // Act
        parser.ProcessLine(testCase.Line);

        // Assert
        var passedMatches = detectedResult != null && detectedResult.PassedTests == testCase.ExpectedPassed;
        var failedMatches = detectedResult != null && detectedResult.FailedTests == testCase.ExpectedFailed;
        var totalMatches = detectedResult != null && detectedResult.TotalTests == testCase.ExpectedTotal;

        return passedMatches && failedMatches && totalMatches;
    }

    // Test case classes

    public class StateMarkerTestCase
    {
        public required string Line { get; init; }
        public required KiroCliLib.Models.KiroState ExpectedState { get; init; }
    }

    public class FileOperationTestCase
    {
        public required string Line { get; init; }
        public required string ExpectedPath { get; init; }
        public required KiroCliLib.Models.FileChangeType ExpectedType { get; init; }
    }

    public class TestResultTestCase
    {
        public required string Line { get; init; }
        public required int ExpectedPassed { get; init; }
        public required int ExpectedFailed { get; init; }
        public required int ExpectedTotal { get; init; }
    }

    // Arbitrary generators
    public static class Generators
    {
        public static Arbitrary<StateMarkerTestCase> StateMarkerTestCase()
        {
            var completionMarkers = new[]
            {
                ("✓ Task completed", KiroCliLib.Models.KiroState.Completed),
                ("Done processing all files", KiroCliLib.Models.KiroState.Completed),
                ("Completed successfully", KiroCliLib.Models.KiroState.Completed),
                ("Success: all checks passed", KiroCliLib.Models.KiroState.Completed),
                ("✔ All done", KiroCliLib.Models.KiroState.Completed)
            };

            var errorMarkers = new[]
            {
                ("Error: Something went wrong", KiroCliLib.Models.KiroState.Error),
                ("Failed: build returned non-zero", KiroCliLib.Models.KiroState.Error),
                ("Exception: NullReferenceException", KiroCliLib.Models.KiroState.Error),
                ("✗ Task failed", KiroCliLib.Models.KiroState.Error),
                ("✘ Build error detected", KiroCliLib.Models.KiroState.Error)
            };

            var inputMarkers = new[]
            {
                ("? Please provide input", KiroCliLib.Models.KiroState.NeedsInput),
                ("Clarification needed for this step", KiroCliLib.Models.KiroState.NeedsInput),
                ("Please provide more details", KiroCliLib.Models.KiroState.NeedsInput),
                ("Waiting for input from user", KiroCliLib.Models.KiroState.NeedsInput)
            };

            var phaseMarkers = new[]
            {
                ("Starting research phase", KiroCliLib.Models.KiroState.ResearchPhase),
                ("Researching phase begins", KiroCliLib.Models.KiroState.ResearchPhase),
                ("Planning phase started", KiroCliLib.Models.KiroState.PlanPhase),
                ("Creating plan for feature", KiroCliLib.Models.KiroState.PlanPhase),
                ("Implementing feature-xyz", KiroCliLib.Models.KiroState.ImplementPhase),
                ("Implementation phase in progress", KiroCliLib.Models.KiroState.ImplementPhase),
                ("Running tests now", KiroCliLib.Models.KiroState.TestPhase),
                ("Testing phase started", KiroCliLib.Models.KiroState.TestPhase)
            };

            var allMarkers = completionMarkers
                .Concat(errorMarkers)
                .Concat(inputMarkers)
                .Concat(phaseMarkers)
                .ToArray();

            return Gen.Elements(allMarkers)
                .Select(marker => new OutputParserTests.StateMarkerTestCase
                {
                    Line = marker.Item1,
                    ExpectedState = marker.Item2
                })
                .ToArbitrary();
        }

        public static Arbitrary<FileOperationTestCase> FileOperationTestCase()
        {
            var paths = new[]
            {
                "src/Program.cs",
                "tests/UnitTest.cs",
                "config/appsettings.json",
                "docs/README.md",
                "/home/user/project/file.txt",
                "C:\\Projects\\MyApp\\file.cs"
            };

            var createdGen = Gen.Elements(paths)
                .Select(path => new OutputParserTests.FileOperationTestCase
                {
                    Line = $"Created: {path}",
                    ExpectedPath = path,
                    ExpectedType = KiroCliLib.Models.FileChangeType.Created
                });

            var modifiedGen = Gen.Elements(paths)
                .Select(path => new OutputParserTests.FileOperationTestCase
                {
                    Line = $"Modified: {path}",
                    ExpectedPath = path,
                    ExpectedType = KiroCliLib.Models.FileChangeType.Modified
                });

            var writingGen = Gen.Elements(paths)
                .Select(path => new OutputParserTests.FileOperationTestCase
                {
                    Line = $"Writing to {path}",
                    ExpectedPath = path,
                    ExpectedType = KiroCliLib.Models.FileChangeType.Modified
                });

            return Gen.OneOf(createdGen, modifiedGen, writingGen).ToArbitrary();
        }

        public static Arbitrary<TestResultTestCase> TestResultTestCase()
        {
            var testPatternGen = Gen.Choose(0, 100)
                .SelectMany(passed => Gen.Choose(0, 20)
                    .Select(failed => new OutputParserTests.TestResultTestCase
                    {
                        Line = $"Tests: {passed} passed, {failed} failed",
                        ExpectedPassed = passed,
                        ExpectedFailed = failed,
                        ExpectedTotal = passed + failed
                    }));

            var simplePatternGen = Gen.Choose(0, 100)
                .Select(total => new OutputParserTests.TestResultTestCase
                {
                    Line = $"✓ {total} tests",
                    ExpectedPassed = total,
                    ExpectedFailed = 0,
                    ExpectedTotal = total
                });

            return Gen.OneOf(testPatternGen, simplePatternGen).ToArbitrary();
        }
    }
}
