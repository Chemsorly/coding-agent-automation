using System.Diagnostics;
using System.Globalization;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Parsers;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Validates generated code against quality thresholds by running
/// dotnet build and dotnet test in the workspace directory.
/// Uses TRX and Cobertura XML reports for accurate test/coverage data.
/// Optionally validates against an external CI/CD pipeline.
/// </summary>
public class QualityGateValidator : IQualityGateValidator
{
    private readonly Serilog.ILogger _logger;

    public QualityGateValidator(Serilog.ILogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public virtual async Task<QualityGateReport> ValidateAsync(
        string workspacePath, IReadOnlyList<QualityGateConfiguration> qualityGateConfigs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(qualityGateConfigs);

        // Clean up any leftover TestResults from previous quality gate iterations
        var testResultsRoot = Path.GetFullPath(Path.Combine(workspacePath, "TestResults"));
        try
        {
            if (Directory.Exists(testResultsRoot))
            {
                Directory.Delete(testResultsRoot, recursive: true);
                _logger.Debug("Cleaned up previous test results at {TestResultsRoot}", testResultsRoot);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to clean up previous test results at {TestResultsRoot}", testResultsRoot);
        }

        // Clear quality gate output directory so the agent only sees output from this run
        var qualityGatesDir = Path.Combine(workspacePath, PromptBuilder.QualityGatesOutputDirectory);
        try
        {
            if (Directory.Exists(qualityGatesDir))
                Directory.Delete(qualityGatesDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to clean up quality gates output at {QualityGatesDir}", qualityGatesDir);
        }

        var qgcResults = new List<QgcExecutionResult>();

        foreach (var qgc in qualityGateConfigs)
        {
            var compilationResult = await RunQgcCompilationAsync(workspacePath, qgc, ct);
            GateResult? testsResult = null;
            GateResult? coverageResult = null;

            if (compilationResult is { Passed: false })
            {
                qgcResults.Add(new QgcExecutionResult
                {
                    QgcId = qgc.Id,
                    DisplayName = qgc.DisplayName,
                    Compilation = compilationResult,
                    Tests = null,
                    Coverage = null,
                    SecurityScan = null
                });
                break; // Stop on first failure
            }

            testsResult = await RunQgcTestsAsync(workspacePath, qgc, ct);

            if (testsResult is { Passed: false })
            {
                qgcResults.Add(new QgcExecutionResult
                {
                    QgcId = qgc.Id,
                    DisplayName = qgc.DisplayName,
                    Compilation = compilationResult,
                    Tests = testsResult,
                    Coverage = null,
                    SecurityScan = null
                });
                break; // Stop on first failure
            }

            // Coverage threshold check
            if (qgc.CoverageThreshold is > 0)
            {
                coverageResult = ParseCoverageFromReports(workspacePath, qgc);

                if (coverageResult is { Passed: false })
                {
                    qgcResults.Add(new QgcExecutionResult
                    {
                        QgcId = qgc.Id,
                        DisplayName = qgc.DisplayName,
                        Compilation = compilationResult,
                        Tests = testsResult,
                        Coverage = coverageResult,
                        SecurityScan = null
                    });
                    break; // Stop on first failure
                }
            }

            qgcResults.Add(new QgcExecutionResult
            {
                QgcId = qgc.Id,
                DisplayName = qgc.DisplayName,
                Compilation = compilationResult,
                Tests = testsResult,
                Coverage = coverageResult,
                SecurityScan = null
            });
        }

        // Build aggregate flat fields for backward compatibility
        var allCompilationsPassed = qgcResults.All(r => r.Compilation?.Passed ?? true);
        var allTestsPassed = qgcResults.All(r => r.Tests?.Passed ?? true);
        var firstFailingQgc = qgcResults.FirstOrDefault(r => !r.Passed);

        var aggregateCompilation = new GateResult
        {
            GateName = "Compilation",
            Passed = allCompilationsPassed,
            Details = allCompilationsPassed
                ? "All QGC compilations passed"
                : $"Compilation failed in QGC '{firstFailingQgc?.DisplayName}'"
        };

        var totalTestsPassed = qgcResults.Sum(r => r.Tests?.TestsPassed ?? 0);
        var totalTestsFailed = qgcResults.Sum(r => r.Tests?.TestsFailed ?? 0);
        var totalTestsSkipped = qgcResults.Sum(r => r.Tests?.TestsSkipped ?? 0);

        var aggregateTests = new GateResult
        {
            GateName = "Tests",
            Passed = allTestsPassed,
            Details = allTestsPassed
                ? $"All QGC tests passed: {totalTestsPassed} passed, {totalTestsFailed} failed, {totalTestsSkipped} skipped"
                : $"Tests failed in QGC '{firstFailingQgc?.DisplayName}'",
            TestsPassed = totalTestsPassed,
            TestsFailed = totalTestsFailed,
            TestsSkipped = totalTestsSkipped
        };

        // Aggregate coverage: take the first non-null coverage result
        var aggregateCoverage = qgcResults
            .Select(r => r.Coverage)
            .FirstOrDefault(c => c != null);

        return new QualityGateReport
        {
            Compilation = aggregateCompilation,
            Tests = aggregateTests,
            Coverage = aggregateCoverage,
            SecurityScan = null,
            QgcResults = qgcResults
        };
    }

    /// <summary>
    /// Runs the compilation command for a single QGC. Returns null if no compilation command is defined.
    /// </summary>
    private async Task<GateResult?> RunQgcCompilationAsync(
        string workspacePath, QualityGateConfiguration qgc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(qgc.CompilationCommand))
            return null;

        var arguments = qgc.CompilationArguments != null
            ? string.Join(" ", qgc.CompilationArguments)
            : string.Empty;

        var (exitCode, stdout, stderr) = await RunProcessAsync(
            qgc.CompilationCommand, arguments, workspacePath, ct);

        WriteGateOutput(workspacePath, $"{qgc.DisplayName}-compilation", stdout, stderr);

        string details;
        if (exitCode == ExitCodes.Success)
        {
            details = "Build succeeded";
        }
        else
        {
            var (errors, warnings) = ParseBuildErrorCounts(stdout + "\n" + stderr);
            details = $"Build failed with exit code {exitCode}. {errors} error(s), {warnings} warning(s).";
        }

        return new GateResult
        {
            GateName = "Compilation",
            Passed = exitCode == ExitCodes.Success,
            Details = details
        };
    }

    /// <summary>
    /// Runs the test command for a single QGC. Returns null if no test command is defined.
    /// Only appends .NET-specific flags (--logger trx, --results-directory, --collect) when
    /// the test command is "dotnet". For other languages (python, mvn, etc.), the test arguments
    /// are used as-is and test counts are parsed from stdout.
    /// </summary>
    private async Task<GateResult?> RunQgcTestsAsync(
        string workspacePath, QualityGateConfiguration qgc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(qgc.TestCommand))
            return null;

        var arguments = qgc.TestArguments != null
            ? string.Join(" ", qgc.TestArguments)
            : string.Empty;

        var isDotnet = string.Equals(qgc.TestCommand, "dotnet", StringComparison.OrdinalIgnoreCase);
        var collectCoverage = qgc.CoverageThreshold is > 0;
        string? resultsDir = null;
        string fullArgs;

        if (isDotnet)
        {
            // .NET: Add TRX logger and results directory for test result parsing
            resultsDir = Path.GetFullPath(Path.Combine(workspacePath, "TestResults", $"qg-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(resultsDir);

            fullArgs = $"{arguments} --logger trx --results-directory \"{resultsDir}\"";
            if (collectCoverage && string.Equals(qgc.CoverageReportFormat, "cobertura", StringComparison.OrdinalIgnoreCase))
                fullArgs += " --collect:\"XPlat Code Coverage\"";
        }
        else
        {
            // Non-.NET: Use test arguments as-is (coverage flags should be in TestArguments)
            fullArgs = arguments;
        }

        var (exitCode, stdout, stderr) = await RunProcessAsync(
            qgc.TestCommand, fullArgs, workspacePath, ct);

        WriteGateOutput(workspacePath, $"{qgc.DisplayName}-tests", stdout, stderr);

        // Parse test counts
        int passed, failed, skipped;
        if (isDotnet && resultsDir != null)
        {
            // Parse TRX files for accurate test counts
            (passed, failed, skipped) = ParseTestCountsFromTrx(resultsDir);

            // If TRX parsing found nothing, fall back to stdout parsing
            if (passed == 0 && failed == 0 && skipped == 0)
            {
                _logger.Warning("No TRX results found in {ResultsDir} for QGC {QgcName}, falling back to stdout parsing",
                    resultsDir, qgc.DisplayName);
                (passed, failed, skipped) = ParseTestCountsFromStdout(stdout);
            }
        }
        else
        {
            // Non-.NET: parse test counts from stdout
            (passed, failed, skipped) = ParseTestCountsFromStdout(stdout);
        }

        _logger.Information("QGC {QgcName} test results: {Passed} passed, {Failed} failed, {Skipped} skipped",
            qgc.DisplayName, passed, failed, skipped);

        // Clean up results directory (non-fatal) — skip if coverage was collected,
        // because ParseCoverageFromReports needs the Cobertura XML files afterward.
        if (isDotnet && resultsDir != null && !collectCoverage)
        {
            try
            {
                if (Directory.Exists(resultsDir))
                    Directory.Delete(resultsDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to clean up test results directory {ResultsDir}", resultsDir);
            }
        }

        return new GateResult
        {
            GateName = "Tests",
            Passed = exitCode == ExitCodes.Success,
            Details = exitCode == ExitCodes.Success
                ? $"Tests passed: {passed} passed, {failed} failed, {skipped} skipped"
                : $"Tests failed: {passed} passed, {failed} failed, {skipped} skipped.",
            TestsPassed = passed,
            TestsFailed = failed,
            TestsSkipped = skipped
        };
    }

    /// <summary>
    /// Formats a short CI failure summary for GateResult.Details.
    /// Verbose per-job logs are in .agent/quality-gates/ — the retry prompt points there.
    /// </summary>
    internal static string BuildCiFailureDetails(
        PipelineRunStatus status, IReadOnlyDictionary<long, string>? logPathMapping = null)
    {
        var failedJobs = status.Jobs.Where(j => j.State == PipelineRunState.Failed).ToList();
        var jobNames = failedJobs.Count > 0
            ? string.Join(", ", failedJobs.Select(j => $"'{j.Name}'"))
            : "unknown";
        return $"CI {status.State}. {failedJobs.Count} job(s) failed: {jobNames}.";
    }

    /// <summary>
    /// Parses all .trx files in the results directory and sums up test counts across all assemblies.
    /// TRX files contain a ResultSummary/Counters element with total/passed/failed/etc attributes.
    /// </summary>
    internal static (int Passed, int Failed, int Skipped) ParseTestCountsFromTrx(string resultsDir)
        => TrxTestResultParser.ParseTestCounts(resultsDir);

    /// <summary>
    /// Locates and parses coverage reports based on the QGC configuration.
    /// Supports Cobertura XML (default) and JaCoCo XML formats.
    /// Uses CoverageReportPaths if specified, otherwise falls back to convention-based discovery.
    /// </summary>
    private GateResult ParseCoverageFromReports(string workspacePath, QualityGateConfiguration qgc)
    {
        var threshold = qgc.CoverageThreshold!.Value;
        var format = qgc.CoverageReportFormat ?? "cobertura";
        var isDotnet = string.Equals(qgc.TestCommand, "dotnet", StringComparison.OrdinalIgnoreCase);

        // Discover coverage report files
        var reportFiles = DiscoverCoverageReportFiles(workspacePath, qgc.CoverageReportPaths, format, isDotnet);

        if (reportFiles.Length == 0)
        {
            _logger.Warning("No {Format} coverage files found in {WorkspacePath}", format, workspacePath);
            return new GateResult
            {
                GateName = "Coverage",
                Passed = false,
                Details = $"No coverage data — {format} XML not found",
                CoveragePercent = null
            };
        }

        // Parse based on format
        var coveragePercent = string.Equals(format, "jacoco", StringComparison.OrdinalIgnoreCase)
            ? ParseCoverageFromJacoco(reportFiles)
            : ParseCoverageFromCobertura(reportFiles);

        _logger.Information("Coverage ({Format}): {CoveragePercent:F1}% (threshold: {Threshold:F1}%)",
            format, coveragePercent, threshold);

        var passed = coveragePercent >= threshold;
        return new GateResult
        {
            GateName = "Coverage",
            Passed = passed,
            Details = passed
                ? $"Coverage {coveragePercent.ToString("F1", CultureInfo.InvariantCulture)}% meets threshold {threshold.ToString("F1", CultureInfo.InvariantCulture)}%"
                : $"Coverage {coveragePercent.ToString("F1", CultureInfo.InvariantCulture)}% below threshold {threshold.ToString("F1", CultureInfo.InvariantCulture)}%",
            CoveragePercent = coveragePercent
        };
    }

    /// <summary>
    /// Discovers coverage report files based on explicit paths or convention-based defaults.
    /// </summary>
    private string[] DiscoverCoverageReportFiles(
        string workspacePath, IReadOnlyList<string>? explicitPaths, string format, bool isDotnet)
    {
        if (explicitPaths is { Count: > 0 })
        {
            // Use explicit paths (relative to workspace root)
            var files = new List<string>();
            foreach (var pattern in explicitPaths)
            {
                var fullPattern = Path.Combine(workspacePath, pattern);
                var dir = Path.GetDirectoryName(fullPattern) ?? workspacePath;
                var filePattern = Path.GetFileName(fullPattern);

                if (Directory.Exists(dir))
                {
                    files.AddRange(Directory.GetFiles(dir, filePattern, SearchOption.TopDirectoryOnly));
                }
                else
                {
                    // Try recursive search from workspace root with the pattern as filename
                    files.AddRange(Directory.GetFiles(workspacePath, filePattern, SearchOption.AllDirectories)
                        .Where(f => f.Replace('\\', '/').Contains(pattern.Replace('\\', '/'))));
                }
            }
            return files.ToArray();
        }

        // Convention-based discovery
        if (isDotnet)
        {
            // .NET: coverlet outputs to TestResults/**/coverage.cobertura.xml
            var resultsDir = Path.GetFullPath(Path.Combine(workspacePath, "TestResults"));
            return Directory.Exists(resultsDir)
                ? Directory.GetFiles(resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories)
                : [];
        }

        if (string.Equals(format, "jacoco", StringComparison.OrdinalIgnoreCase))
        {
            // Java/Maven: JaCoCo outputs to target/site/jacoco/jacoco.xml
            var files = Directory.GetFiles(workspacePath, "jacoco.xml", SearchOption.AllDirectories);
            return files;
        }

        // Non-.NET Cobertura (e.g., Python pytest-cov): search for coverage.xml or *.cobertura.xml
        var coberturaFiles = new List<string>();
        coberturaFiles.AddRange(Directory.GetFiles(workspacePath, "coverage.xml", SearchOption.AllDirectories));
        coberturaFiles.AddRange(Directory.GetFiles(workspacePath, "*.cobertura.xml", SearchOption.AllDirectories));
        return coberturaFiles.Distinct().ToArray();
    }

    /// <summary>
    /// Parses Cobertura XML files and returns the merged line coverage percentage.
    /// When multiple reports cover the same source file, line-level hits are merged
    /// (max hit count per line) to avoid double-counting in multi-project solutions.
    /// </summary>
    internal static double ParseCoverageFromCobertura(string[] coberturaFiles)
        => CoberturaParser.ParseCoverage(coberturaFiles);

    /// <summary>
    /// Parses JaCoCo XML files and returns the aggregate line coverage percentage.
    /// JaCoCo uses counter elements with type="LINE" at the class level:
    /// <![CDATA[<counter type="LINE" missed="6" covered="10"/>]]>
    /// When multiple reports cover the same source file, counters are summed.
    /// </summary>
    internal static double ParseCoverageFromJacoco(string[] jacocoFiles)
        => JacocoParser.ParseCoverage(jacocoFiles);

    /// <summary>
    /// Fallback: parses test counts from stdout when TRX files are not available.
    /// Handles .NET per-assembly format, .NET 10 summary line, pytest output, and Maven/JUnit output.
    /// </summary>
    internal static (int Passed, int Failed, int Skipped) ParseTestCountsFromStdout(string output)
        => StdoutTestResultParser.ParseTestCounts(output);

    /// <summary>
    /// Writes gate stdout/stderr to .agent/quality-gates/{gateName}-stdout.txt and
    /// {gateName}-stderr.txt so the agent can read them on demand.
    /// </summary>
    private void WriteGateOutput(string workspacePath, string gateName, string? stdout, string? stderr)
    {
        try
        {
            var dir = Path.Combine(workspacePath, PromptBuilder.QualityGatesOutputDirectory);
            Directory.CreateDirectory(dir);
            if (!string.IsNullOrEmpty(stdout))
                File.WriteAllText(Path.Combine(dir, $"{gateName}-stdout.txt"), stdout);
            if (!string.IsNullOrEmpty(stderr))
                File.WriteAllText(Path.Combine(dir, $"{gateName}-stderr.txt"), stderr);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to write quality gate output for {GateName}", gateName);
        }
    }

    /// <summary>
    /// Parses error and warning counts from MSBuild output.
    /// Looks for the summary line pattern: "X Error(s)" and "Y Warning(s)".
    /// </summary>
    internal static (int Errors, int Warnings) ParseBuildErrorCounts(string output)
        => BuildOutputParser.ParseBuildErrorCounts(output);

    private protected virtual async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }
}
