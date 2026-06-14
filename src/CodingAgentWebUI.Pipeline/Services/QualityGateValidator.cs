using System.Diagnostics;
using System.Globalization;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Parsers;
using CodingAgentWebUI.Pipeline.Telemetry;
using KiroCliLib.Core;
using OpenTelemetry.Trace;

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
        string workspacePath, IReadOnlyList<QualityGateConfiguration> qualityGateConfigs, CancellationToken ct, string? baseBranch = null)
    {
        return await ValidateAsync(workspacePath, qualityGateConfigs, null, ct, baseBranch);
    }

    /// <summary>
    /// Validates quality gates with optional branch-awareness for quarantine filtering.
    /// </summary>
    public virtual async Task<QualityGateReport> ValidateAsync(
        string workspacePath, IReadOnlyList<QualityGateConfiguration> qualityGateConfigs, IReadOnlyList<string>? branchModifiedFiles, CancellationToken ct, string? baseBranch = null)
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
        var qualityGatesDir = Path.Combine(workspacePath, AgentWorkspacePaths.QualityGatesOutputDirectory);
        try
        {
            if (Directory.Exists(qualityGatesDir))
                Directory.Delete(qualityGatesDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to clean up quality gates output at {QualityGatesDir}", qualityGatesDir);
        }

        // Compute branch modified files for quarantine branch-awareness if not provided
        var hasQuarantine = qualityGateConfigs.Any(q => q.TestQuarantine is { Enabled: true });
        if (branchModifiedFiles == null && hasQuarantine)
        {
            branchModifiedFiles = await ComputeBranchModifiedFilesAsync(workspacePath, baseBranch, ct);
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

            testsResult = await RunQgcTestsAsync(workspacePath, qgc, branchModifiedFiles, ct);

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
        var totalTestsQuarantined = qgcResults.Sum(r => r.Tests?.TestsQuarantined ?? 0);
        var allQuarantinedNames = qgcResults
            .Where(r => r.Tests?.QuarantinedTestNames != null)
            .SelectMany(r => r.Tests!.QuarantinedTestNames!)
            .ToList();

        var testsDetails = allTestsPassed
            ? $"All QGC tests passed: {totalTestsPassed} passed, {totalTestsFailed} failed, {totalTestsSkipped} skipped"
            : $"Tests failed in QGC '{firstFailingQgc?.DisplayName}'";
        if (totalTestsQuarantined > 0)
            testsDetails += $" ({totalTestsQuarantined} quarantined)";

        var aggregateTests = new GateResult
        {
            GateName = "Tests",
            Passed = allTestsPassed,
            Details = testsDetails,
            TestsPassed = totalTestsPassed,
            TestsFailed = totalTestsFailed,
            TestsSkipped = totalTestsSkipped,
            TestsQuarantined = totalTestsQuarantined > 0 ? totalTestsQuarantined : null,
            QuarantinedTestNames = allQuarantinedNames.Count > 0 ? allQuarantinedNames : null
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

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("QualityGate.Compilation");
        activity?.SetTag("gate_name", "compilation");

        try
        {
            var arguments = qgc.CompilationArguments != null
                ? string.Join(" ", qgc.CompilationArguments)
                : string.Empty;

            var timeout = TimeSpan.FromSeconds(qgc.ProcessTimeoutSeconds);
            var (exitCode, stdout, stderr) = await RunProcessAsync(
                qgc.CompilationCommand, arguments, workspacePath, ct, timeout);

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
        catch (TimeoutException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return new GateResult
            {
                GateName = "Compilation",
                Passed = false,
                Details = $"Compilation timed out after {qgc.ProcessTimeoutSeconds}s"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            throw;
        }
    }

    /// <summary>
    /// Runs the test command for a single QGC. Returns null if no test command is defined.
    /// Only appends .NET-specific flags (--logger trx, --results-directory, --collect) when
    /// the test command is "dotnet". For other languages (python, mvn, etc.), the test arguments
    /// are used as-is and test counts are parsed from stdout.
    /// </summary>
    private async Task<GateResult?> RunQgcTestsAsync(
        string workspacePath, QualityGateConfiguration qgc, IReadOnlyList<string>? branchModifiedFiles, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(qgc.TestCommand))
            return null;

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("QualityGate.Tests");
        activity?.SetTag("gate_name", "tests");

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

        int exitCode;
        string stdout, stderr;
        try
        {
            (exitCode, stdout, stderr) = await RunProcessAsync(
                qgc.TestCommand, fullArgs, workspacePath, ct, TimeSpan.FromSeconds(qgc.ProcessTimeoutSeconds));
        }
        catch (TimeoutException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return new GateResult
            {
                GateName = "Tests",
                Passed = false,
                Details = $"Tests timed out after {qgc.ProcessTimeoutSeconds}s"
            };
        }

        WriteGateOutput(workspacePath, $"{qgc.DisplayName}-tests", stdout, stderr);

        // Parse test counts
        int passed, failed, skipped;
        IReadOnlyList<string> failedTestNames = [];
        if (isDotnet && resultsDir != null)
        {
            // Parse TRX files for accurate test counts and individual failed test names
            var trxResult = TrxTestResultParser.ParseTestResults(resultsDir);
            passed = trxResult.Passed;
            failed = trxResult.Failed;
            skipped = trxResult.Skipped;
            failedTestNames = trxResult.FailedTestNames;

            // If TRX parsing found nothing, fall back to stdout parsing
            if (passed == 0 && failed == 0 && skipped == 0)
            {
                _logger.Warning("No TRX results found in {ResultsDir} for QGC {QgcName}, falling back to stdout parsing",
                    resultsDir, qgc.DisplayName);
                (passed, failed, skipped) = ParseTestCountsFromStdout(stdout);
                failedTestNames = [];
            }
        }
        else
        {
            // Non-.NET: parse test counts from stdout
            (passed, failed, skipped) = ParseTestCountsFromStdout(stdout);
        }

        _logger.Information("QGC {QgcName} test results: {Passed} passed, {Failed} failed, {Skipped} skipped",
            qgc.DisplayName, passed, failed, skipped);

        // Apply quarantine filtering (only when TRX provides individual test names)
        var quarantinedNames = new List<string>();
        var gatePassed = exitCode == ExitCodes.Success;

        if (!gatePassed && failedTestNames.Count > 0 && qgc.TestQuarantine is { Enabled: true })
        {
            var filterResult = ApplyQuarantineFilter(failedTestNames, qgc.TestQuarantine, branchModifiedFiles, DateTime.UtcNow);

            if (!filterResult.SafetyValveTriggered)
            {
                quarantinedNames.AddRange(filterResult.QuarantinedTestNames);
                var nonQuarantinedFailures = failedTestNames.Count - quarantinedNames.Count;
                gatePassed = nonQuarantinedFailures == 0;
                failed = nonQuarantinedFailures;

                if (quarantinedNames.Count > 0)
                {
                    _logger.Information("QGC {QgcName} quarantined {Count} flaky test(s): {Tests}",
                        qgc.DisplayName, quarantinedNames.Count, string.Join(", ", quarantinedNames));
                }
            }
            else
            {
                _logger.Warning("QGC {QgcName} exceeded MaxQuarantinedFailuresPerRun ({Max}), treating all {Count} quarantined failures as real",
                    qgc.DisplayName, qgc.TestQuarantine.MaxQuarantinedFailuresPerRun, filterResult.QuarantinedTestNames.Count);
            }
        }

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

        var details = gatePassed
            ? $"Tests passed: {passed} passed, {failed} failed, {skipped} skipped"
            : $"Tests failed: {passed} passed, {failed} failed, {skipped} skipped.";
        if (quarantinedNames.Count > 0)
            details += $" ({quarantinedNames.Count} quarantined)";

        return new GateResult
        {
            GateName = "Tests",
            Passed = gatePassed,
            Details = details,
            TestsPassed = passed,
            TestsFailed = failed,
            TestsSkipped = skipped,
            TestsQuarantined = quarantinedNames.Count > 0 ? quarantinedNames.Count : null,
            QuarantinedTestNames = quarantinedNames.Count > 0 ? quarantinedNames : null
        };
    }

    /// <summary>
    /// Applies quarantine filtering to a set of failed test names.
    /// Returns which tests are quarantined and whether the safety valve was triggered.
    /// </summary>
    internal static QuarantineFilterResult ApplyQuarantineFilter(
        IReadOnlyList<string> failedTestNames,
        TestQuarantineConfiguration quarantine,
        IReadOnlyList<string>? branchModifiedFiles,
        DateTime utcNow)
    {
        var quarantinedNames = new List<string>();

        foreach (var failedTest in failedTestNames)
        {
            var entry = quarantine.QuarantinedTests.FirstOrDefault(q =>
                string.Equals(q.TestName, failedTest, StringComparison.Ordinal));

            if (entry == null) continue;

            // Check expiry
            if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value < utcNow) continue;

            // Check branch-awareness: if associated source files were modified, lift quarantine
            if (branchModifiedFiles != null && entry.AssociatedSourceFiles is { Count: > 0 })
            {
                var sourceModified = entry.AssociatedSourceFiles.Any(src =>
                    branchModifiedFiles.Any(mod => IsPathSuffixMatch(mod, src) || IsPathSuffixMatch(src, mod)));
                if (sourceModified) continue;
            }

            quarantinedNames.Add(failedTest);
        }

        var safetyValveTriggered = quarantinedNames.Count > quarantine.MaxQuarantinedFailuresPerRun;
        return new QuarantineFilterResult(quarantinedNames, safetyValveTriggered);
    }

    /// <summary>
    /// Checks whether <paramref name="suffix"/> matches as a path suffix of <paramref name="fullPath"/>,
    /// ensuring the match starts at a path separator boundary to avoid false positives
    /// (e.g., "Service.cs" should not match "MyService.cs").
    /// </summary>
    internal static bool IsPathSuffixMatch(string fullPath, string suffix)
    {
        if (string.Equals(fullPath, suffix, StringComparison.OrdinalIgnoreCase))
            return true;

        var normalizedFull = fullPath.Replace('\\', '/');
        var normalizedSuffix = suffix.Replace('\\', '/');

        if (!normalizedFull.EndsWith(normalizedSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        // Ensure the match starts at a path separator boundary
        var prefixLength = normalizedFull.Length - normalizedSuffix.Length;
        return prefixLength == 0 || normalizedFull[prefixLength - 1] == '/';
    }

    internal sealed record QuarantineFilterResult(
        IReadOnlyList<string> QuarantinedTestNames,
        bool SafetyValveTriggered);

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
    {
        var result = TrxTestResultParser.ParseTestResults(resultsDir);
        return (result.Passed, result.Failed, result.Skipped);
    }

    /// <summary>
    /// Locates and parses coverage reports based on the QGC configuration.
    /// Supports Cobertura XML (default) and JaCoCo XML formats.
    /// Uses CoverageReportPaths if specified, otherwise falls back to convention-based discovery.
    /// </summary>
    private GateResult ParseCoverageFromReports(string workspacePath, QualityGateConfiguration qgc)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("QualityGate.Coverage");
        activity?.SetTag("gate_name", "coverage");

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
            var dir = Path.Combine(workspacePath, AgentWorkspacePaths.QualityGatesOutputDirectory);
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

    /// <summary>
    /// Computes the list of files modified by the current branch relative to the base branch.
    /// Uses git diff --name-only. Returns null if git is not available or the command fails.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ComputeBranchModifiedFilesAsync(string workspacePath, string? baseBranch, CancellationToken ct)
    {
        try
        {
            var (exitCode, stdout, _) = await RunProcessAsync(
                "git", $"diff --name-only origin/{baseBranch ?? "main"}...HEAD", workspacePath, ct, TimeSpan.FromSeconds(30));

            if (exitCode != 0)
            {
                _logger.Debug("git diff failed with exit code {ExitCode}, quarantine branch-awareness disabled", exitCode);
                return null;
            }

            return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to compute branch modified files, quarantine branch-awareness disabled");
            return null;
        }
    }

    private protected virtual async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments, string workingDirectory, CancellationToken ct, TimeSpan timeout)
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

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            // TODO: stdoutTask/stderrTask use original ct with no secondary timeout — could hang if Kill fails to release pipe handles
            try { await stdoutTask; } catch { }
            try { await stderrTask; } catch { }
            throw new TimeoutException($"Process '{fileName} {arguments}' timed out after {timeout.TotalSeconds}s");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }
}
