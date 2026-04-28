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
    public void ParseSecurityScanOutput_WithVulnerabilities_DetectsCount()
    {
        var output = "Project A has the following vulnerable packages\nProject B has the following vulnerable packages";
        var (hasVuln, count) = QualityGateValidator.ParseSecurityScanOutput(output);
        hasVuln.Should().BeTrue();
        count.Should().Be(2);
    }

    [Fact]
    public void ParseSecurityScanOutput_NoVulnerabilities_ReturnsFalse()
    {
        var (hasVuln, count) = QualityGateValidator.ParseSecurityScanOutput("No packages with known vulnerabilities");
        hasVuln.Should().BeFalse();
        count.Should().Be(0);
    }

    [Fact]
    public void ParseSecurityScanOutput_NullInput_ReturnsFalse()
    {
        var (hasVuln, count) = QualityGateValidator.ParseSecurityScanOutput(null);
        hasVuln.Should().BeFalse();
        count.Should().Be(0);
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
    public void ParseSecurityScanOutput_EmptyString_ReturnsFalse()
    {
        var (hasVuln, count) = QualityGateValidator.ParseSecurityScanOutput("");
        hasVuln.Should().BeFalse();
        count.Should().Be(0);
    }

    [Fact]
    public void ParseTestCountsFromStdout_NoMatchingLines_ReturnsZeros()
    {
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout("Build succeeded.");
        passed.Should().Be(0);
        failed.Should().Be(0);
        skipped.Should().Be(0);
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

    [Fact]
    public void ParseSecurityScanOutput_SingleVulnerability_ReturnsOne()
    {
        var output = "Project X has the following vulnerable packages";
        var (hasVuln, count) = QualityGateValidator.ParseSecurityScanOutput(output);
        hasVuln.Should().BeTrue();
        count.Should().Be(1);
    }
}
