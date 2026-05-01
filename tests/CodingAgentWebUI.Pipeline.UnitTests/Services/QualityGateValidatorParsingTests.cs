using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Tests for QualityGateValidator static parsing methods.
/// These methods are also tested in Infrastructure.UnitTests, but that project's
/// coverlet config only measures Infrastructure assembly coverage. These tests
/// ensure Pipeline assembly coverage is measured for these methods.
/// </summary>
public class QualityGateValidatorParsingTests : IDisposable
{
    private readonly string _tempDir;

    public QualityGateValidatorParsingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"qg-parse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }

    [Fact]
    public void ParseTestCountsFromStdout_DotNet10Summary_ParsesCorrectly()
    {
        var output = "Test summary: total: 47; failed: 2; succeeded: 44; skipped: 1";
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);
        passed.Should().Be(44);
        failed.Should().Be(2);
        skipped.Should().Be(1);
    }

    [Fact]
    public void ParseTestCountsFromStdout_PerAssemblyFormat_SumsCorrectly()
    {
        var output = "Passed:  10, Failed:   2, Skipped:   1\nPassed:  5, Failed:   0, Skipped:   3";
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);
        passed.Should().Be(15);
        failed.Should().Be(2);
        skipped.Should().Be(4);
    }

    [Fact]
    public void ParseTestCountsFromStdout_EmptyInput_ReturnsZeros()
    {
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout("");
        passed.Should().Be(0);
        failed.Should().Be(0);
        skipped.Should().Be(0);
    }

    [Fact]
    public void ParseBuildErrorCounts_WithErrorsAndWarnings_ParsesCorrectly()
    {
        var output = "Build FAILED.\n    3 Error(s)\n    5 Warning(s)";
        var (errors, warnings) = QualityGateValidator.ParseBuildErrorCounts(output);
        errors.Should().Be(3);
        warnings.Should().Be(5);
    }

    [Fact]
    public void ParseBuildErrorCounts_EmptyInput_ReturnsZeros()
    {
        var (errors, warnings) = QualityGateValidator.ParseBuildErrorCounts("");
        errors.Should().Be(0);
        warnings.Should().Be(0);
    }

    [Fact]
    public void BuildCiFailureDetails_WithFailedJobs_ListsJobNames()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new List<PipelineJobResult>
            {
                new() { Name = "build", State = PipelineRunState.Failed },
                new() { Name = "test", State = PipelineRunState.Passed },
                new() { Name = "lint", State = PipelineRunState.Failed }
            }
        };
        var details = QualityGateValidator.BuildCiFailureDetails(status);
        details.Should().Contain("'build'");
        details.Should().Contain("'lint'");
        details.Should().NotContain("'test'");
        details.Should().Contain("2 job(s) failed");
    }

    [Fact]
    public void ParseCoverageFromCobertura_WithValidFile_ReturnsCorrectPercentage()
    {
        var coberturaXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0.75">
              <packages>
                <package>
                  <classes>
                    <class name="MyClass" filename="MyClass.cs">
                      <lines>
                        <line number="1" hits="1"/>
                        <line number="2" hits="1"/>
                        <line number="3" hits="1"/>
                        <line number="4" hits="0"/>
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;
        var filePath = Path.Combine(_tempDir, "coverage.cobertura.xml");
        File.WriteAllText(filePath, coberturaXml);

        var result = QualityGateValidator.ParseCoverageFromCobertura([filePath]);
        result.Should().Be(75.0);
    }

    [Fact]
    public void ParseCoverageFromCobertura_MergesDuplicateFiles()
    {
        // Two reports covering the same file — should merge (max hits per line)
        var xml1 = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage><packages><package><classes>
              <class name="A" filename="A.cs">
                <lines><line number="1" hits="1"/><line number="2" hits="0"/></lines>
              </class>
            </classes></package></packages></coverage>
            """;
        var xml2 = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage><packages><package><classes>
              <class name="A" filename="A.cs">
                <lines><line number="1" hits="0"/><line number="2" hits="1"/></lines>
              </class>
            </classes></package></packages></coverage>
            """;
        var f1 = Path.Combine(_tempDir, "cov1.xml");
        var f2 = Path.Combine(_tempDir, "cov2.xml");
        File.WriteAllText(f1, xml1);
        File.WriteAllText(f2, xml2);

        var result = QualityGateValidator.ParseCoverageFromCobertura([f1, f2]);
        result.Should().Be(100.0); // Both lines covered after merge
    }

    [Fact]
    public void ParseTestCountsFromTrx_WithValidTrxFile_ParsesCorrectly()
    {
        var trxXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary>
                <Counters total="10" passed="8" failed="1" error="0" notExecuted="1"/>
              </ResultSummary>
            </TestRun>
            """;
        File.WriteAllText(Path.Combine(_tempDir, "results.trx"), trxXml);

        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(_tempDir);
        passed.Should().Be(8);
        failed.Should().Be(1);
        skipped.Should().Be(1);
    }

    [Fact]
    public void ParseTestCountsFromTrx_MissingDirectory_ReturnsZeros()
    {
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx("/nonexistent");
        passed.Should().Be(0);
        failed.Should().Be(0);
        skipped.Should().Be(0);
    }

    [Fact]
    public void ParseTestCountsFromTrx_NoTrxFiles_ReturnsZeros()
    {
        var emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(emptyDir);
        passed.Should().Be(0);
        failed.Should().Be(0);
        skipped.Should().Be(0);
    }

    [Fact]
    public void ParseTestCountsFromTrx_WithErrorAttribute_CountsAsFailure()
    {
        var trxXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary>
                <Counters total="5" passed="3" failed="0" error="2" notExecuted="0"/>
              </ResultSummary>
            </TestRun>
            """;
        File.WriteAllText(Path.Combine(_tempDir, "errors.trx"), trxXml);

        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(_tempDir);
        passed.Should().Be(3);
        failed.Should().Be(2); // error counts as failed
        skipped.Should().Be(0);
    }

    [Fact]
    public void ParseTestCountsFromTrx_MultipleTrxFiles_SumsAcrossFiles()
    {
        var trx1 = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary><Counters total="5" passed="5" failed="0" error="0" notExecuted="0"/></ResultSummary>
            </TestRun>
            """;
        var trx2 = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary><Counters total="3" passed="2" failed="1" error="0" notExecuted="0"/></ResultSummary>
            </TestRun>
            """;
        File.WriteAllText(Path.Combine(_tempDir, "a.trx"), trx1);
        File.WriteAllText(Path.Combine(_tempDir, "b.trx"), trx2);

        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(_tempDir);
        passed.Should().Be(7);
        failed.Should().Be(1);
        skipped.Should().Be(0);
    }

    [Fact]
    public void ParseCoverageFromCobertura_EmptyFiles_ReturnsZero()
    {
        var result = QualityGateValidator.ParseCoverageFromCobertura([]);
        result.Should().Be(0.0);
    }

    [Fact]
    public void ParseCoverageFromCobertura_MalformedXml_SkipsFile()
    {
        var badFile = Path.Combine(_tempDir, "bad.xml");
        File.WriteAllText(badFile, "not xml");
        var goodXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage><packages><package><classes>
              <class name="A" filename="A.cs">
                <lines><line number="1" hits="1"/><line number="2" hits="1"/></lines>
              </class>
            </classes></package></packages></coverage>
            """;
        var goodFile = Path.Combine(_tempDir, "good.xml");
        File.WriteAllText(goodFile, goodXml);

        var result = QualityGateValidator.ParseCoverageFromCobertura([badFile, goodFile]);
        result.Should().Be(100.0);
    }

    [Fact]
    public void BuildCiFailureDetails_NoFailedJobs_ShowsUnknown()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new List<PipelineJobResult>
            {
                new() { Name = "build", State = PipelineRunState.Passed }
            }
        };
        var details = QualityGateValidator.BuildCiFailureDetails(status);
        details.Should().Contain("0 job(s) failed");
        details.Should().Contain("unknown");
    }

    [Fact]
    public void ParseBuildErrorCounts_SuccessfulBuild_ReturnsZeros()
    {
        var output = "Build succeeded.\n    0 Error(s)\n    0 Warning(s)";
        var (errors, warnings) = QualityGateValidator.ParseBuildErrorCounts(output);
        errors.Should().Be(0);
        warnings.Should().Be(0);
    }

    [Fact]
    public void ParseTestCountsFromStdout_NoMatchingLines_ReturnsZeros()
    {
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout("Build succeeded.");
        passed.Should().Be(0);
        failed.Should().Be(0);
        skipped.Should().Be(0);
    }

    // --- Pytest Stdout Parsing Tests ---

    [Fact]
    public void ParseTestCountsFromStdout_PytestAllPassed_ParsesCorrectly()
    {
        var output = "========================= 5 passed in 1.23s =========================";
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);
        passed.Should().Be(5);
        failed.Should().Be(0);
        skipped.Should().Be(0);
    }

    [Fact]
    public void ParseTestCountsFromStdout_PytestMixed_ParsesCorrectly()
    {
        var output = "=================== 3 passed, 2 failed, 1 skipped in 4.56s ===================";
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);
        passed.Should().Be(3);
        failed.Should().Be(2);
        skipped.Should().Be(1);
    }

    [Fact]
    public void ParseTestCountsFromStdout_PytestWithErrors_CountsErrorsAsFailed()
    {
        var output = "=================== 5 passed, 1 error in 2.00s ===================";
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);
        passed.Should().Be(5);
        failed.Should().Be(1);
        skipped.Should().Be(0);
    }

    // --- Maven/JUnit Stdout Parsing Tests ---

    [Fact]
    public void ParseTestCountsFromStdout_MavenSingleModule_ParsesCorrectly()
    {
        var output = "Tests run: 10, Failures: 2, Errors: 1, Skipped: 3";
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);
        passed.Should().Be(4); // 10 - 2 - 1 - 3
        failed.Should().Be(3); // 2 failures + 1 error
        skipped.Should().Be(3);
    }

    [Fact]
    public void ParseTestCountsFromStdout_MavenMultiModule_SumsAcrossModules()
    {
        var output = """
            [INFO] Results:
            Tests run: 5, Failures: 0, Errors: 0, Skipped: 0
            [INFO] Results:
            Tests run: 8, Failures: 1, Errors: 0, Skipped: 2
            """;
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);
        passed.Should().Be(10); // (5-0-0-0) + (8-1-0-2) = 5 + 5
        failed.Should().Be(1);
        skipped.Should().Be(2);
    }

    [Fact]
    public void ParseCoverageFromCobertura_MissingHitsAttribute_TreatsAsZero()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage><packages><package><classes>
              <class name="A" filename="A.cs">
                <lines><line number="1"/><line number="2" hits="1"/></lines>
              </class>
            </classes></package></packages></coverage>
            """;
        var filePath = Path.Combine(_tempDir, "nohits.xml");
        File.WriteAllText(filePath, xml);

        var result = QualityGateValidator.ParseCoverageFromCobertura([filePath]);
        result.Should().Be(50.0); // 1 of 2 lines covered
    }

    // --- JaCoCo Coverage Parsing Tests ---

    [Fact]
    public void ParseCoverageFromJacoco_WithValidFile_ReturnsCorrectPercentage()
    {
        var jacocoXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <report name="MyProject">
              <package name="com/example">
                <class name="com/example/MyClass" sourcefilename="MyClass.java">
                  <method name="doSomething" desc="()V" line="10">
                    <counter type="INSTRUCTION" missed="5" covered="10"/>
                    <counter type="LINE" missed="2" covered="8"/>
                  </method>
                  <counter type="INSTRUCTION" missed="5" covered="10"/>
                  <counter type="LINE" missed="2" covered="8"/>
                </class>
              </package>
              <counter type="INSTRUCTION" missed="5" covered="10"/>
              <counter type="LINE" missed="2" covered="8"/>
            </report>
            """;
        var filePath = Path.Combine(_tempDir, "jacoco.xml");
        File.WriteAllText(filePath, jacocoXml);

        var result = QualityGateValidator.ParseCoverageFromJacoco([filePath]);
        result.Should().Be(80.0); // 8 covered / (8 + 2 missed) = 80%
    }

    [Fact]
    public void ParseCoverageFromJacoco_MultipleClasses_SumsCounters()
    {
        var jacocoXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <report name="MyProject">
              <package name="com/example">
                <class name="com/example/ClassA" sourcefilename="ClassA.java">
                  <counter type="LINE" missed="5" covered="15"/>
                </class>
                <class name="com/example/ClassB" sourcefilename="ClassB.java">
                  <counter type="LINE" missed="10" covered="10"/>
                </class>
              </package>
            </report>
            """;
        var filePath = Path.Combine(_tempDir, "jacoco.xml");
        File.WriteAllText(filePath, jacocoXml);

        var result = QualityGateValidator.ParseCoverageFromJacoco([filePath]);
        // ClassA: 15/(15+5)=75%, ClassB: 10/(10+10)=50%, Total: 25/(25+15)=62.5%
        result.Should().Be(62.5);
    }

    [Fact]
    public void ParseCoverageFromJacoco_IgnoresNonClassCounters()
    {
        // Only class-level LINE counters should be counted, not package or report-level
        var jacocoXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <report name="MyProject">
              <package name="com/example">
                <class name="com/example/MyClass" sourcefilename="MyClass.java">
                  <counter type="LINE" missed="3" covered="7"/>
                </class>
                <counter type="LINE" missed="3" covered="7"/>
              </package>
              <counter type="LINE" missed="3" covered="7"/>
            </report>
            """;
        var filePath = Path.Combine(_tempDir, "jacoco.xml");
        File.WriteAllText(filePath, jacocoXml);

        var result = QualityGateValidator.ParseCoverageFromJacoco([filePath]);
        // Only class-level counter: 7/(7+3) = 70%
        result.Should().Be(70.0);
    }

    [Fact]
    public void ParseCoverageFromJacoco_EmptyFiles_ReturnsZero()
    {
        var result = QualityGateValidator.ParseCoverageFromJacoco([]);
        result.Should().Be(0.0);
    }

    [Fact]
    public void ParseCoverageFromJacoco_MalformedXml_SkipsFile()
    {
        var badFile = Path.Combine(_tempDir, "bad.xml");
        File.WriteAllText(badFile, "not xml at all");

        var goodXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <report name="MyProject">
              <package name="com/example">
                <class name="com/example/MyClass" sourcefilename="MyClass.java">
                  <counter type="LINE" missed="0" covered="10"/>
                </class>
              </package>
            </report>
            """;
        var goodFile = Path.Combine(_tempDir, "good.xml");
        File.WriteAllText(goodFile, goodXml);

        var result = QualityGateValidator.ParseCoverageFromJacoco([badFile, goodFile]);
        result.Should().Be(100.0);
    }

    [Fact]
    public void ParseCoverageFromJacoco_MultipleFiles_SumsAcrossFiles()
    {
        var xml1 = """
            <?xml version="1.0" encoding="UTF-8"?>
            <report name="Module1">
              <package name="com/example">
                <class name="com/example/ClassA" sourcefilename="ClassA.java">
                  <counter type="LINE" missed="0" covered="10"/>
                </class>
              </package>
            </report>
            """;
        var xml2 = """
            <?xml version="1.0" encoding="UTF-8"?>
            <report name="Module2">
              <package name="com/example">
                <class name="com/example/ClassB" sourcefilename="ClassB.java">
                  <counter type="LINE" missed="10" covered="0"/>
                </class>
              </package>
            </report>
            """;
        var f1 = Path.Combine(_tempDir, "jacoco1.xml");
        var f2 = Path.Combine(_tempDir, "jacoco2.xml");
        File.WriteAllText(f1, xml1);
        File.WriteAllText(f2, xml2);

        var result = QualityGateValidator.ParseCoverageFromJacoco([f1, f2]);
        // 10 covered / (10 + 10 missed) = 50%
        result.Should().Be(50.0);
    }

    [Fact]
    public void ParseCoverageFromJacoco_IgnoresNonLineCounterTypes()
    {
        var jacocoXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <report name="MyProject">
              <package name="com/example">
                <class name="com/example/MyClass" sourcefilename="MyClass.java">
                  <counter type="INSTRUCTION" missed="100" covered="0"/>
                  <counter type="BRANCH" missed="50" covered="0"/>
                  <counter type="COMPLEXITY" missed="20" covered="0"/>
                  <counter type="METHOD" missed="10" covered="0"/>
                  <counter type="LINE" missed="2" covered="8"/>
                </class>
              </package>
            </report>
            """;
        var filePath = Path.Combine(_tempDir, "jacoco.xml");
        File.WriteAllText(filePath, jacocoXml);

        var result = QualityGateValidator.ParseCoverageFromJacoco([filePath]);
        // Only LINE counter matters: 8/(8+2) = 80%
        result.Should().Be(80.0);
    }
}
