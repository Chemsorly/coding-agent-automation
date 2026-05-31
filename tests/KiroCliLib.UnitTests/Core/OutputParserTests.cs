using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using KiroCliLib.Core;
using KiroCliLib.Models;
using TestResult = KiroCliLib.Models.TestResult;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Property-based tests for OutputParser.
/// Migrated from CodingAgentWebUI.IntegrationTests as part of KiroCliLib test separation.
/// Validates: State detection, test result parsing.
/// </summary>
public class OutputParserTests
{
    /// <summary>
    /// Property: For any Kiro CLI output containing state markers,
    /// the OutputParser should correctly identify the state.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(Generators) })]
    public bool StateDetection_ShouldIdentifyCorrectState(StateMarkerTestCase testCase)
    {
        var parser = new OutputParser();
        KiroState? detectedState = null;
        parser.StateChanged += (_, state) => detectedState = state;

        parser.ProcessLine(testCase.Line);

        var result = detectedState == testCase.ExpectedState;
        if (!result)
            throw new Exception($"State detection failed for line: '{testCase.Line}'. Expected: {testCase.ExpectedState}, Got: {detectedState}");
        return result;
    }

    /// <summary>
    /// Property: For any Kiro CLI output containing test results,
    /// the OutputParser should correctly extract passed/failed/total counts.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(Generators) })]
    public bool TestResultDetection_ShouldExtractCounts(TestResultTestCase testCase)
    {
        var parser = new OutputParser();

        parser.ProcessLine(testCase.Line);

        var detectedResult = parser.TestResults;
        return detectedResult != null
            && detectedResult.PassedTests == testCase.ExpectedPassed
            && detectedResult.FailedTests == testCase.ExpectedFailed
            && detectedResult.TotalTests == testCase.ExpectedTotal;
    }

    [Fact]
    public void ProcessLine_NullInput_ThrowsArgumentNullException()
    {
        var parser = new OutputParser();
        Assert.Throws<ArgumentNullException>(() => parser.ProcessLine(null!));
    }

    [Fact]
    public void ProcessLine_EmptyOrWhitespace_DoesNotFireEvents()
    {
        var parser = new OutputParser();
        var eventFired = false;
        parser.StateChanged += (_, _) => eventFired = true;

        parser.ProcessLine("");
        parser.ProcessLine("   ");
        parser.ProcessLine("\t");

        Assert.False(eventFired);
    }

    [Fact]
    public void ProcessLine_NoMatchingPattern_DoesNotFireEvents()
    {
        var parser = new OutputParser();
        var eventFired = false;
        parser.StateChanged += (_, _) => eventFired = true;

        parser.ProcessLine("Just a regular log line with no markers");

        Assert.False(eventFired);
        Assert.Null(parser.TestResults);
    }

    [Fact]
    public void ProcessLine_SameStateRepeated_DoesNotFireAgain()
    {
        var parser = new OutputParser();
        var fireCount = 0;
        parser.StateChanged += (_, _) => fireCount++;

        parser.ProcessLine("✓ Task completed");
        parser.ProcessLine("✓ Another completion");

        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void TestResults_Property_ReturnsLastDetectedResult()
    {
        var parser = new OutputParser();

        Assert.Null(parser.TestResults);

        parser.ProcessLine("Tests: 10 passed, 2 failed");
        Assert.NotNull(parser.TestResults);
        Assert.Equal(12, parser.TestResults!.TotalTests);

        parser.ProcessLine("Tests: 15 passed, 0 failed");
        Assert.Equal(15, parser.TestResults!.TotalTests);
    }

    // Test case classes

    public class StateMarkerTestCase
    {
        public required string Line { get; init; }
        public required KiroState ExpectedState { get; init; }
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
            var allMarkers = new (string, KiroState)[]
            {
                ("✓ Task completed", KiroState.Completed),
                ("Done processing all files", KiroState.Completed),
                ("Completed successfully", KiroState.Completed),
                ("Success: all checks passed", KiroState.Completed),
                ("✔ All done", KiroState.Completed),
                ("Error: Something went wrong", KiroState.Error),
                ("Failed: build returned non-zero", KiroState.Error),
                ("Exception: NullReferenceException", KiroState.Error),
                ("✗ Task failed", KiroState.Error),
                ("✘ Build error detected", KiroState.Error),
                ("? Please provide input", KiroState.NeedsInput),
                ("Clarification needed for this step", KiroState.NeedsInput),
                ("Please provide more details", KiroState.NeedsInput),
                ("Waiting for input from user", KiroState.NeedsInput),
                ("Starting research phase", KiroState.ResearchPhase),
                ("Researching phase begins", KiroState.ResearchPhase),
                ("Planning phase started", KiroState.PlanPhase),
                ("Creating plan for feature", KiroState.PlanPhase),
                ("Implementing feature-xyz", KiroState.ImplementPhase),
                ("Implementation phase in progress", KiroState.ImplementPhase),
                ("Running tests now", KiroState.TestPhase),
                ("Testing phase started", KiroState.TestPhase),
            };

            return Gen.Elements(allMarkers)
                .Select(m => new StateMarkerTestCase { Line = m.Item1, ExpectedState = m.Item2 })
                .ToArbitrary();
        }

        public static Arbitrary<TestResultTestCase> TestResultTestCase()
        {
            var testPatternGen = Gen.Choose(0, 100)
                .SelectMany(passed => Gen.Choose(0, 20)
                    .Select(failed => new TestResultTestCase
                    {
                        Line = $"Tests: {passed} passed, {failed} failed",
                        ExpectedPassed = passed, ExpectedFailed = failed, ExpectedTotal = passed + failed
                    }));

            var simplePatternGen = Gen.Choose(0, 100)
                .Select(total => new TestResultTestCase
                {
                    Line = $"✓ {total} tests",
                    ExpectedPassed = total, ExpectedFailed = 0, ExpectedTotal = total
                });

            return Gen.OneOf(testPatternGen, simplePatternGen).ToArbitrary();
        }
    }
}
