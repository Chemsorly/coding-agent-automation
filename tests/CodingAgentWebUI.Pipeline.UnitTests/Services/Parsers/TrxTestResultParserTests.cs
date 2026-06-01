using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services.Parsers;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class TrxTestResultParserTests
{
    [Fact]
    public void ParseTestResults_WithFailedTests_ReturnsFailedTestNames()
    {
        var dir = CreateTrxDir("""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary>
                <Counters total="3" passed="1" failed="2" error="0" notExecuted="0" />
              </ResultSummary>
              <Results>
                <UnitTestResult testName="Namespace.Class.PassingTest" outcome="Passed" />
                <UnitTestResult testName="Namespace.Class.FailingTest1" outcome="Failed" />
                <UnitTestResult testName="Namespace.Class.FailingTest2" outcome="Failed" />
              </Results>
            </TestRun>
            """);

        var result = TrxTestResultParser.ParseTestResults(dir);

        result.Passed.Should().Be(1);
        result.Failed.Should().Be(2);
        result.FailedTestNames.Should().HaveCount(2);
        result.FailedTestNames.Should().Contain("Namespace.Class.FailingTest1");
        result.FailedTestNames.Should().Contain("Namespace.Class.FailingTest2");
    }

    [Fact]
    public void ParseTestResults_WithNoFailures_ReturnsEmptyList()
    {
        var dir = CreateTrxDir("""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary>
                <Counters total="5" passed="5" failed="0" error="0" notExecuted="0" />
              </ResultSummary>
              <Results>
                <UnitTestResult testName="Namespace.Class.Test1" outcome="Passed" />
              </Results>
            </TestRun>
            """);

        var result = TrxTestResultParser.ParseTestResults(dir);

        result.Passed.Should().Be(5);
        result.Failed.Should().Be(0);
        result.FailedTestNames.Should().BeEmpty();
    }

    [Fact]
    public void ParseTestResults_WithMultipleTrxFiles_AggregatesFailedNames()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"trx-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "results1.trx"), """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary>
                <Counters total="2" passed="1" failed="1" error="0" notExecuted="0" />
              </ResultSummary>
              <Results>
                <UnitTestResult testName="Assembly1.FailingTest" outcome="Failed" />
              </Results>
            </TestRun>
            """);

        File.WriteAllText(Path.Combine(dir, "results2.trx"), """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary>
                <Counters total="3" passed="2" failed="1" error="0" notExecuted="0" />
              </ResultSummary>
              <Results>
                <UnitTestResult testName="Assembly2.AnotherFailingTest" outcome="Failed" />
              </Results>
            </TestRun>
            """);

        var result = TrxTestResultParser.ParseTestResults(dir);

        result.Passed.Should().Be(3);
        result.Failed.Should().Be(2);
        result.FailedTestNames.Should().HaveCount(2);
        result.FailedTestNames.Should().Contain("Assembly1.FailingTest");
        result.FailedTestNames.Should().Contain("Assembly2.AnotherFailingTest");

        Directory.Delete(dir, true);
    }

    [Fact]
    public void ParseTestResults_WithMalformedXml_SkipsAndReturnsPartialResults()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"trx-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "good.trx"), """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary>
                <Counters total="1" passed="0" failed="1" error="0" notExecuted="0" />
              </ResultSummary>
              <Results>
                <UnitTestResult testName="Good.FailingTest" outcome="Failed" />
              </Results>
            </TestRun>
            """);

        File.WriteAllText(Path.Combine(dir, "bad.trx"), "not valid xml <<<<");

        var result = TrxTestResultParser.ParseTestResults(dir);

        result.Failed.Should().Be(1);
        result.FailedTestNames.Should().Contain("Good.FailingTest");

        Directory.Delete(dir, true);
    }

    [Fact]
    public void ParseTestResults_PreservesFullyQualifiedTestNames()
    {
        var dir = CreateTrxDir("""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary>
                <Counters total="1" passed="0" failed="1" error="0" notExecuted="0" />
              </ResultSummary>
              <Results>
                <UnitTestResult testName="MyProject.Tests.Integration.DatabaseTests.Connection_WhenTimeout_ThrowsException" outcome="Failed" />
              </Results>
            </TestRun>
            """);

        var result = TrxTestResultParser.ParseTestResults(dir);

        result.FailedTestNames.Should().ContainSingle()
            .Which.Should().Be("MyProject.Tests.Integration.DatabaseTests.Connection_WhenTimeout_ThrowsException");
    }

    [Fact]
    public void ParseTestResults_NonExistentDirectory_ReturnsZeros()
    {
        var result = TrxTestResultParser.ParseTestResults("/nonexistent/path");

        result.Passed.Should().Be(0);
        result.Failed.Should().Be(0);
        result.Skipped.Should().Be(0);
        result.FailedTestNames.Should().BeEmpty();
    }

    [Fact]
    public void ParseTestCounts_DelegatesToParseTestResults()
    {
        var dir = CreateTrxDir("""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary>
                <Counters total="10" passed="7" failed="2" error="0" notExecuted="1" />
              </ResultSummary>
              <Results />
            </TestRun>
            """);

        var (passed, failed, skipped) = TrxTestResultParser.ParseTestCounts(dir);

        passed.Should().Be(7);
        failed.Should().Be(2);
        skipped.Should().Be(1);
    }

    private static string CreateTrxDir(string trxContent)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"trx-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "results.trx"), trxContent);
        return dir;
    }
}
