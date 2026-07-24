using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

public class QualityGateValidatorTests
{
    [Fact]
    public void AllPassed_WhenAllGatesPass_ReturnsTrue()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true },
            Coverage = new GateResult { GateName = "Coverage", Passed = true },
            SecurityScan = new GateResult { GateName = "Security", Passed = true }
        };

        report.AllPassed.Should().BeTrue();
    }

    [Fact]
    public void AllPassed_WhenCompilationFails_ReturnsFalse()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build error" },
            Tests = new GateResult { GateName = "Tests", Passed = true }
        };

        report.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void AllPassed_WhenTestsFail_ReturnsFalse()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = false, TestsFailed = 3 }
        };

        report.AllPassed.Should().BeFalse();
    }

    [Fact]
    public void AllPassed_WithNullOptionalGates_ReturnsTrue()
    {
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true },
            Coverage = null,
            SecurityScan = null
        };

        report.AllPassed.Should().BeTrue();
    }

    // --- TRX Parsing Tests ---

    [Fact]
    public void ParseTestCountsFromTrx_WithValidTrxFile_ExtractsCorrectCounts()
    {
        var dir = CreateTempDir();
        try
        {
            WriteTrxFile(dir, "results.trx", passed: 10, failed: 2, notExecuted: 1);

            var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(dir);

            passed.Should().Be(10);
            failed.Should().Be(2);
            skipped.Should().Be(1);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseTestCountsFromTrx_WithMultipleTrxFiles_SumsAcrossAssemblies()
    {
        var dir = CreateTempDir();
        try
        {
            WriteTrxFile(dir, "assembly1.trx", passed: 10, failed: 0, notExecuted: 1);
            WriteTrxFile(dir, "assembly2.trx", passed: 25, failed: 3, notExecuted: 0);

            var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(dir);

            passed.Should().Be(35);
            failed.Should().Be(3);
            skipped.Should().Be(1);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseTestCountsFromTrx_WithErrorAttribute_CountsAsFailure()
    {
        var dir = CreateTempDir();
        try
        {
            WriteTrxFile(dir, "results.trx", passed: 8, failed: 1, notExecuted: 0, error: 2);

            var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(dir);

            passed.Should().Be(8);
            failed.Should().Be(3); // 1 failed + 2 error
            skipped.Should().Be(0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseTestCountsFromTrx_WithNoDirectory_ReturnsZeros()
    {
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx("/nonexistent/path");

        passed.Should().Be(0);
        failed.Should().Be(0);
        skipped.Should().Be(0);
    }

    [Fact]
    public void ParseTestCountsFromTrx_WithEmptyDirectory_ReturnsZeros()
    {
        var dir = CreateTempDir();
        try
        {
            var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(dir);

            passed.Should().Be(0);
            failed.Should().Be(0);
            skipped.Should().Be(0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseTestCountsFromTrx_WithMalformedXml_SkipsAndReturnsZeros()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.trx"), "not xml at all");

            var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(dir);

            passed.Should().Be(0);
            failed.Should().Be(0);
            skipped.Should().Be(0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseTestCountsFromTrx_WithMixedValidAndMalformed_SumsValidOnly()
    {
        var dir = CreateTempDir();
        try
        {
            WriteTrxFile(dir, "good.trx", passed: 10, failed: 1, notExecuted: 0);
            File.WriteAllText(Path.Combine(dir, "bad.trx"), "not xml");

            var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromTrx(dir);

            passed.Should().Be(10);
            failed.Should().Be(1);
            skipped.Should().Be(0);
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- Cobertura Coverage Parsing Tests ---

    [Fact]
    public void ParseCoverageFromCobertura_WithSingleFile_ReturnsCorrectPercentage()
    {
        var dir = CreateTempDir();
        try
        {
            var file = WriteCoberturaFile(dir, "coverage.cobertura.xml", lineRate: 0.85, linesValid: 200);

            var coverage = QualityGateValidator.ParseCoverageFromCobertura([file]);

            coverage.Should().BeApproximately(85.0, 0.5);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseCoverageFromCobertura_WithMultipleFiles_ReturnsWeightedAverage()
    {
        var dir = CreateTempDir();
        try
        {
            var file1 = WriteCoberturaFile(dir, "cov1.xml", lineRate: 0.9, linesValid: 100);
            var file2 = WriteCoberturaFile(dir, "cov2.xml", lineRate: 0.6, linesValid: 300);

            // Weighted: (90 + 180) / 400 = 67.5%
            var coverage = QualityGateValidator.ParseCoverageFromCobertura([file1, file2]);

            coverage.Should().BeApproximately(67.5, 0.5);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseCoverageFromCobertura_WithNoFiles_ReturnsZero()
    {
        var coverage = QualityGateValidator.ParseCoverageFromCobertura([]);

        coverage.Should().Be(0.0);
    }

    // --- Stdout Fallback Parsing Tests ---

    [Theory]
    [InlineData("Passed:  10, Failed:   2, Skipped:   1", 10, 2, 1)]
    [InlineData("Passed: 0, Failed: 0, Skipped: 0", 0, 0, 0)]
    [InlineData("No test results here", 0, 0, 0)]
    [InlineData("", 0, 0, 0)]
    public void ParseTestCountsFromStdout_PerAssemblyFormat_ExtractsCorrectValues(
        string output, int expectedPassed, int expectedFailed, int expectedSkipped)
    {
        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);

        passed.Should().Be(expectedPassed);
        failed.Should().Be(expectedFailed);
        skipped.Should().Be(expectedSkipped);
    }

    // --- Build Error Count Parsing Tests ---

    [Theory]
    [InlineData("Build FAILED.\n    3 Error(s)\n    2 Warning(s)", 3, 2)]
    [InlineData("Build FAILED.\n    0 Error(s)\n    0 Warning(s)", 0, 0)]
    [InlineData("Build succeeded.\n    0 Error(s)\n    1 Warning(s)", 0, 1)]
    [InlineData("no match here", 0, 0)]
    [InlineData("", 0, 0)]
    public void ParseBuildErrorCounts_ExtractsCorrectValues(
        string output, int expectedErrors, int expectedWarnings)
    {
        var (errors, warnings) = QualityGateValidator.ParseBuildErrorCounts(output);

        errors.Should().Be(expectedErrors);
        warnings.Should().Be(expectedWarnings);
    }

    // --- BuildCiFailureDetails Tests ---

    [Fact]
    public void BuildCiFailureDetails_ReturnsSummaryOnly()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new[]
            {
                new PipelineJobResult { Name = "build-and-test", State = PipelineRunState.Failed, FailureReason = "Process completed with exit code 1", JobId = 123, LogUrl = "https://example.com/logs" },
                new PipelineJobResult { Name = "lint", State = PipelineRunState.Passed, JobId = 456 }
            }
        };

        var details = QualityGateValidator.BuildCiFailureDetails(status);

        details.Should().Contain("1 job(s) failed");
        details.Should().Contain("'build-and-test'");
        // Should NOT contain verbose per-job details, log URLs, or file paths
        details.Should().NotContain("https://example.com/logs");
        details.Should().NotContain("Full CI log saved to");
    }

    [Fact]
    public void ParseTestCountsFromStdout_MultipleAssemblyLines_SumsAll()
    {
        var output = """
            Passed:  10, Failed:   0, Skipped:   1 - Assembly1.dll
            Passed:  25, Failed:   3, Skipped:   0 - Assembly2.dll
            """;

        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);

        passed.Should().Be(35);
        failed.Should().Be(3);
        skipped.Should().Be(1);
    }

    [Fact]
    public void ParseTestCountsFromStdout_DotNet10SummaryLine_ParsesCorrectly()
    {
        var output = "Test summary: total: 47; failed: 0; succeeded: 47; skipped: 0; duration: 1.4s";

        var (passed, failed, skipped) = QualityGateValidator.ParseTestCountsFromStdout(output);

        passed.Should().Be(47);
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

    // --- Cobertura Edge Case Tests ---

    [Fact]
    public void ParseCoverageFromCobertura_MergesDuplicateFiles()
    {
        var dir = CreateTempDir();
        try
        {
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
            var f1 = Path.Combine(dir, "cov1.xml");
            var f2 = Path.Combine(dir, "cov2.xml");
            File.WriteAllText(f1, xml1);
            File.WriteAllText(f2, xml2);

            var result = QualityGateValidator.ParseCoverageFromCobertura([f1, f2]);
            result.Should().Be(100.0); // Both lines covered after merge
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseCoverageFromCobertura_MalformedXml_SkipsFile()
    {
        var dir = CreateTempDir();
        try
        {
            var badFile = Path.Combine(dir, "bad.xml");
            File.WriteAllText(badFile, "not xml");
            var goodXml = """
                <?xml version="1.0" encoding="utf-8"?>
                <coverage><packages><package><classes>
                  <class name="A" filename="A.cs">
                    <lines><line number="1" hits="1"/><line number="2" hits="1"/></lines>
                  </class>
                </classes></package></packages></coverage>
                """;
            var goodFile = Path.Combine(dir, "good.xml");
            File.WriteAllText(goodFile, goodXml);

            var result = QualityGateValidator.ParseCoverageFromCobertura([badFile, goodFile]);
            result.Should().Be(100.0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseCoverageFromCobertura_MissingHitsAttribute_TreatsAsZero()
    {
        var dir = CreateTempDir();
        try
        {
            var xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <coverage><packages><package><classes>
                  <class name="A" filename="A.cs">
                    <lines><line number="1"/><line number="2" hits="1"/></lines>
                  </class>
                </classes></package></packages></coverage>
                """;
            var filePath = Path.Combine(dir, "nohits.xml");
            File.WriteAllText(filePath, xml);

            var result = QualityGateValidator.ParseCoverageFromCobertura([filePath]);
            result.Should().Be(50.0); // 1 of 2 lines covered
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- BuildCiFailureDetails Edge Cases ---

    [Fact]
    public void BuildCiFailureDetails_WithMultipleFailedJobs_ListsAllJobNames()
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

    // --- JaCoCo Coverage Parsing Tests ---

    [Fact]
    public void ParseCoverageFromJacoco_WithValidFile_ReturnsCorrectPercentage()
    {
        var dir = CreateTempDir();
        try
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
            var filePath = Path.Combine(dir, "jacoco.xml");
            File.WriteAllText(filePath, jacocoXml);

            var result = QualityGateValidator.ParseCoverageFromJacoco([filePath]);
            result.Should().Be(80.0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseCoverageFromJacoco_MultipleClasses_SumsCounters()
    {
        var dir = CreateTempDir();
        try
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
            var filePath = Path.Combine(dir, "jacoco.xml");
            File.WriteAllText(filePath, jacocoXml);

            var result = QualityGateValidator.ParseCoverageFromJacoco([filePath]);
            result.Should().Be(62.5);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseCoverageFromJacoco_IgnoresNonClassCounters()
    {
        var dir = CreateTempDir();
        try
        {
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
            var filePath = Path.Combine(dir, "jacoco.xml");
            File.WriteAllText(filePath, jacocoXml);

            var result = QualityGateValidator.ParseCoverageFromJacoco([filePath]);
            result.Should().Be(70.0);
        }
        finally { Directory.Delete(dir, true); }
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
        var dir = CreateTempDir();
        try
        {
            var badFile = Path.Combine(dir, "bad.xml");
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
            var goodFile = Path.Combine(dir, "good.xml");
            File.WriteAllText(goodFile, goodXml);

            var result = QualityGateValidator.ParseCoverageFromJacoco([badFile, goodFile]);
            result.Should().Be(100.0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseCoverageFromJacoco_MultipleFiles_SumsAcrossFiles()
    {
        var dir = CreateTempDir();
        try
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
            var f1 = Path.Combine(dir, "jacoco1.xml");
            var f2 = Path.Combine(dir, "jacoco2.xml");
            File.WriteAllText(f1, xml1);
            File.WriteAllText(f2, xml2);

            var result = QualityGateValidator.ParseCoverageFromJacoco([f1, f2]);
            result.Should().Be(50.0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseCoverageFromJacoco_IgnoresNonLineCounterTypes()
    {
        var dir = CreateTempDir();
        try
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
            var filePath = Path.Combine(dir, "jacoco.xml");
            File.WriteAllText(filePath, jacocoXml);

            var result = QualityGateValidator.ParseCoverageFromJacoco([filePath]);
            result.Should().Be(80.0);
        }
        finally { Directory.Delete(dir, true); }
    }

    // --- Helpers ---

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"qg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteTrxFile(string dir, string fileName,
        int passed, int failed, int notExecuted, int error = 0)
    {
        var total = passed + failed + notExecuted + error;
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Completed">
                <Counters total="{total}" executed="{passed + failed + error}" passed="{passed}" failed="{failed}" error="{error}" notExecuted="{notExecuted}" />
              </ResultSummary>
            </TestRun>
            """;
        File.WriteAllText(Path.Combine(dir, fileName), xml);
    }

    private static string WriteCoberturaFile(string dir, string fileName,
        double lineRate, long linesValid)
    {
        var linesCovered = (long)(lineRate * linesValid);
        var linesUncovered = linesValid - linesCovered;

        // Generate line-level data matching the summary attributes
        var lineElements = new System.Text.StringBuilder();
        for (var i = 1; i <= linesCovered; i++)
            lineElements.AppendLine($"              <line number=\"{i}\" hits=\"1\" />");
        for (var i = (int)linesCovered + 1; i <= linesValid; i++)
            lineElements.AppendLine($"              <line number=\"{i}\" hits=\"0\" />");

        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="{lineRate.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}" lines-valid="{linesValid}" lines-covered="{linesCovered}" version="1.0">
              <packages>
                <package name="TestAssembly">
                  <classes>
                    <class name="TestClass" filename="{fileName}.cs">
                      <lines>
            {lineElements}
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public async Task Compilation_Timeout_ReturnsFailedGateResult()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-timeout-test-{Guid.NewGuid():N}");
        try
        {
            var validator = new TimeoutSimulatingValidator(simulateTimeout: true);
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Test",
                CompilationCommand = "dotnet",
                CompilationArguments = ["build"],
                ProcessTimeoutSeconds = 1
            };

            var report = await validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None);

            report.Compilation.Passed.Should().BeFalse();
            report.QgcResults[0].Compilation!.Details.Should().Contain("timed out");
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    [Fact]
    public async Task Tests_Timeout_ReturnsFailedGateResult()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-timeout-test-{Guid.NewGuid():N}");
        try
        {
            var validator = new TimeoutSimulatingValidator(simulateTimeout: true);
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Test",
                TestCommand = "dotnet",
                TestArguments = ["test"],
                ProcessTimeoutSeconds = 1
            };

            var report = await validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None);

            report.Tests!.Passed.Should().BeFalse();
            report.QgcResults[0].Tests!.Details.Should().Contain("timed out");
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    [Fact]
    public async Task NormalExecution_WithTimeout_CompletesSuccessfully()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"qg-timeout-test-{Guid.NewGuid():N}");
        try
        {
            var validator = new TimeoutSimulatingValidator(simulateTimeout: false);
            var qgc = new QualityGateConfiguration
            {
                DisplayName = "Test",
                CompilationCommand = "dotnet",
                CompilationArguments = ["build"],
                ProcessTimeoutSeconds = 600
            };

            var report = await validator.ValidateAsync(tempWorkspace, [qgc], CancellationToken.None);

            report.Compilation.Passed.Should().BeTrue();
            report.Compilation.Details.Should().NotContain("timed out");
        }
        finally { try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, true); } catch { } }
    }

    // TODO: Missing test for bounded pipe drain timeout. A process that holds stdout/stderr pipes
    // open after being killed (e.g., grandchild inheriting handles) should still allow the method
    // to return within ~5s due to the drain CancellationTokenSource.
    [Fact]
    public async Task RunProcessAsync_ExternalCancellation_KillsProcessAndThrowsOperationCanceledException()
    {
        // Arrange: create a validator that exposes the real RunProcessAsync
        var validator = new ProcessExposingValidator();
        using var cts = new CancellationTokenSource();

        // Act: spawn a long-running process (sleep 300s) and cancel after a brief delay
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        var act = () => validator.RunProcessPublicAsync(
            "sleep", "300", Directory.GetCurrentDirectory(), cts.Token, TimeSpan.FromMinutes(10));

        // Assert: OperationCanceledException is thrown within bounded time.
        // The method must complete promptly (kill + 5s drain timeout at most) — if the process
        // wasn't killed, ReadToEndAsync would block until the 300s sleep finishes.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Stop();

        // Must complete well within the drain timeout window (5s) + cancel delay (500ms) + margin.
        // If Kill didn't work, this would take 300s (the sleep duration).
        // Use 30s threshold to avoid flaky failures under CI load while still catching real hangs.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    private sealed class ProcessExposingValidator : QualityGateValidator
    {
        private readonly TimeSpan? _pipeDrainTimeout;

        public ProcessExposingValidator(TimeSpan? pipeDrainTimeout = null) : base(Serilog.Log.Logger)
        {
            _pipeDrainTimeout = pipeDrainTimeout;
        }

        protected override TimeSpan PipeDrainTimeout => _pipeDrainTimeout ?? base.PipeDrainTimeout;

        public Task<(int ExitCode, string Stdout, string Stderr)> RunProcessPublicAsync(
            string fileName, string arguments, string workingDirectory, CancellationToken ct, TimeSpan timeout)
            => RunProcessAsync(fileName, arguments, workingDirectory, ct, timeout);
    }

    // TODO: BothPipesComplete exercises only the happy path where both pipes close instantly.
    // It cannot distinguish between sequential and concurrent drain since no timeout pressure exists.
    // It serves as a regression guard for the refactored structure.
    [Fact]
    public async Task RunProcessAsync_NormalPath_PipeDrainConcurrent_BothPipesComplete()
    {
        // Arrange: spawn a process that writes to both stdout and stderr then exits cleanly
        var validator = new ProcessExposingValidator();
        var script = "echo 'hello_stdout'; echo 'hello_stderr' >&2";

        // Act
        var (exitCode, stdout, stderr) = await validator.RunProcessPublicAsync(
            "bash", $"-c \"{script}\"", Directory.GetCurrentDirectory(), CancellationToken.None, TimeSpan.FromSeconds(30));

        // Assert: both streams captured correctly
        exitCode.Should().Be(0);
        stdout.Trim().Should().Be("hello_stdout");
        stderr.Trim().Should().Be("hello_stderr");
    }

    // TODO: This test does not distinguish old sequential code from the new concurrent code.
    // The catch-block fallback preserved completed pipes in both patterns. To truly validate
    // the fix, add a test where stdout completes at time X (0 < X < timeout) and stderr
    // completes at time Y where Y > timeout−X but Y < timeout (e.g., timeout=5s, stdout at 3s,
    // stderr at 4s). Old sequential code would lose stderr; new concurrent code preserves it.
    [Fact]
    public async Task RunProcessAsync_NormalPath_PipeDrainTimeout_PreservesCompletedPipe()
    {
        // Arrange: Use a short pipe drain timeout (5s) to make the test fast and meaningful.
        // Spawn a process where stderr closes after ~2s (via a grandchild that holds it briefly)
        // but stdout is held open indefinitely by another grandchild.
        //
        // With concurrent drain (Task.WhenAll + fallback):
        //   - Both WaitAsync calls start at t=0
        //   - stderrTask completes at ~t=2s (pipe closes when short-lived grandchild exits)
        //   - stdoutTask never completes (grandchild sleeps forever)
        //   - CTS fires at t=5s → Task.WhenAll throws OperationCanceledException
        //   - Fallback: stderrTask.IsCompletedSuccessfully=true → stderr preserved ✓
        //
        // With hypothetical sequential per-pipe try/catch (the old bug pattern):
        //   - await stdoutTask.WaitAsync(cts) → CTS fires at t=5s → stdout = string.Empty
        //   - await stderrTask.WaitAsync(cts) → CTS already cancelled → immediate throw → stderr = string.Empty
        //   - Both lost!
        //
        // Key: the test uses a 5s timeout, and the method must complete in ~5s (not ~10s which
        // would indicate sequential per-pipe timeouts).
        var pipeDrainTimeout = TimeSpan.FromSeconds(5);
        var validator = new ProcessExposingValidator(pipeDrainTimeout);

        // Script: 
        //   1. Fork grandchild A that holds stdout open forever (sleep 300, inherits stdout)
        //   2. Fork grandchild B that holds stderr open for ~2s then exits (releases stderr)
        //   3. Write expected content to both pipes from main process
        //   4. Exit main process immediately — triggers pipe drain path
        // Grandchild A: inherits stdout (no redirect), closes stderr (2>/dev/null)
        // Grandchild B: inherits stderr (no redirect), closes stdout (1>/dev/null), sleeps 2s then exits
        var script = "(sleep 300 2>/dev/null &); (sleep 2 1>/dev/null &); echo 'expected_stdout'; echo 'expected_stderr' >&2; exit 0";

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await validator.RunProcessPublicAsync(
            "bash", $"-c \"{script}\"", Directory.GetCurrentDirectory(), CancellationToken.None, TimeSpan.FromSeconds(30));
        sw.Stop();

        // Assert: method returns within a reasonable bound. The pipe drain timeout is 5s, but
        // process creation, bash startup, and subshell forking add variable overhead in CI.
        // Use a generous bound (15s) that still catches degenerate behavior while tolerating
        // slow environments. The primary value of this test is the functional assertion below.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));

        // Assert: stderr content is preserved — the concurrent drain + fallback ensures that
        // stderr (which completed before the timeout) is not lost when stdout times out.
        stderr.Trim().Should().Be("expected_stderr");

        // Assert: process exited cleanly
        exitCode.Should().Be(0);
    }

    // TODO: This test does not distinguish old sequential code from new concurrent code.
    // The old code shared ONE CTS across sequential awaits (not independent per-pipe timeouts),
    // so both old and new code complete in ~5s. The comment about "~10s for sequential" describes
    // a pattern that never existed. Consider a test that demonstrates the actual difference:
    // stdout completing partway through the timeout, with stderr needing the remaining time.
    [Fact]
    public async Task RunProcessAsync_NormalPath_PipeDrainTimeout_CompletesWithinBoundedTime()
    {
        // Arrange: Use a short pipe drain timeout (5s). Spawn a process where BOTH stdout and
        // stderr are held open indefinitely by a grandchild.
        //
        // This validates that concurrent drain (Task.WhenAll) completes in ~1x timeout,
        // not ~2x timeout (which would happen if each pipe were drained with its own
        // independent sequential timeout).
        var pipeDrainTimeout = TimeSpan.FromSeconds(5);
        var validator = new ProcessExposingValidator(pipeDrainTimeout);

        // Script: fork a grandchild that inherits both pipes and sleeps forever, then exit.
        var script = "sleep 300 & exit 0";

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await validator.RunProcessPublicAsync(
            "bash", $"-c \"{script}\"", Directory.GetCurrentDirectory(), CancellationToken.None, TimeSpan.FromSeconds(30));
        sw.Stop();

        // Assert: bounded completion within 1x pipe drain timeout + generous margin for CI.
        // The pipe drain timeout is 5s; process creation and scheduling overhead in loaded
        // CI environments can add several seconds. Use 15s as the upper bound — still well
        // below what truly sequential drain would produce (10s drain + overhead).
        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(4)); // must actually wait for timeout
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));   // but not 2x timeout
        exitCode.Should().Be(0);
    }

    // TODO: Add a test that exercises the actual bug scenario: stdout completes partway through
    // the timeout (e.g., at 3s with a 5s timeout), and stderr completes at a time greater than
    // timeout−stdout_time but less than timeout (e.g., at 4s). With old sequential code, stderr
    // would only get 2s (5−3) and be lost. With new concurrent code, stderr gets the full 5s
    // and is preserved. Without this test, reverting the fix passes all existing tests.

    private sealed class TimeoutSimulatingValidator : QualityGateValidator
    {
        private readonly bool _simulateTimeout;

        public TimeoutSimulatingValidator(bool simulateTimeout) : base(Serilog.Log.Logger)
        {
            _simulateTimeout = simulateTimeout;
        }

        private protected override Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
            string fileName, string arguments, string workingDirectory, CancellationToken ct, TimeSpan timeout)
        {
            if (_simulateTimeout)
                throw new TimeoutException($"Process '{fileName} {arguments}' timed out after {timeout.TotalSeconds}s");

            return Task.FromResult((0, "Build succeeded.", ""));
        }
    }
}


