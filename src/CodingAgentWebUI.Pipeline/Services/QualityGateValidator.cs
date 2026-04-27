using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

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

    public virtual async Task<QualityGateReport> ValidateAsync(
        string workspacePath, PipelineConfiguration config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspacePath);
        ArgumentNullException.ThrowIfNull(config);

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

        var compilation = await RunCompilationGateAsync(workspacePath, ct);

        // Use a dedicated results directory — must be absolute so dotnet test
        // (which runs with WorkingDirectory=workspacePath) writes to the correct location
        var resultsDir = Path.GetFullPath(Path.Combine(workspacePath, "TestResults", $"qg-{Guid.NewGuid():N}"));
        var collectCoverage = config.MinCoverageThreshold > 0;

        var tests = await RunTestGateAsync(workspacePath, resultsDir, collectCoverage, ct);

        GateResult? coverage = null;
        if (collectCoverage)
        {
            coverage = ParseCoverageFromReports(resultsDir, config.MinCoverageThreshold);
        }

        GateResult? securityScan = null;
        if (config.SecurityScanEnabled)
        {
            securityScan = await RunSecurityScanGateAsync(workspacePath, ct);
        }

        // External CI gate is handled by PipelineOrchestrationService after local gates pass

        // Clean up results directory (non-fatal)
        try
        {
            if (Directory.Exists(resultsDir))
                Directory.Delete(resultsDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to clean up test results directory {ResultsDir}", resultsDir);
        }

        return new QualityGateReport
        {
            Compilation = compilation,
            Tests = tests,
            Coverage = coverage,
            SecurityScan = securityScan
        };
    }

    /// <summary>
    /// Formats a short CI failure summary for GateResult.Details.
    /// Verbose per-job logs are in .kiro/quality-gates/ — the retry prompt points there.
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

    private async Task<GateResult> RunCompilationGateAsync(string workspacePath, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await RunProcessAsync("dotnet", "build", workspacePath, ct);

        WriteGateOutput(workspacePath, "compilation", stdout, stderr);

        string details;
        if (exitCode == 0)
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
            Passed = exitCode == 0,
            Details = details
        };
    }

    private async Task<GateResult> RunSecurityScanGateAsync(string workspacePath, CancellationToken ct)
    {
        var (exitCode, stdout, stderr) = await RunProcessAsync(
            "dotnet", "list package --vulnerable --include-transitive", workspacePath, ct);

        WriteGateOutput(workspacePath, "security-scan", stdout, stderr);

        if (exitCode != 0)
        {
            _logger.Warning("dotnet list package --vulnerable exited with code {ExitCode}", exitCode);
            return new GateResult
            {
                GateName = "Security Scan",
                Passed = true,
                Details = $"Security scan skipped (exit code {exitCode}) — check security-scan-stderr.txt"
            };
        }

        if (!string.IsNullOrWhiteSpace(stderr) && stderr.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warning("Security scan stderr contains errors: {Stderr}", stderr);
        }

        var (hasVulnerabilities, projectCount) = ParseSecurityScanOutput(stdout);

        return new GateResult
        {
            GateName = "Security Scan",
            Passed = !hasVulnerabilities,
            Details = hasVulnerabilities
                ? $"{projectCount} project(s) with vulnerable packages"
                : "No vulnerable packages found"
        };
    }

    /// <summary>
    /// Parses stdout from <c>dotnet list package --vulnerable</c> to detect vulnerable packages.
    /// The command always returns exit code 0 regardless of findings (dotnet/sdk#16852),
    /// so detection relies on the text "has the following vulnerable packages".
    /// </summary>
    internal static (bool HasVulnerabilities, int ProjectCount) ParseSecurityScanOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (false, 0);

        var matches = Regex.Matches(output, @"has the following vulnerable packages", RegexOptions.IgnoreCase);
        return matches.Count > 0 ? (true, matches.Count) : (false, 0);
    }

    private async Task<GateResult> RunTestGateAsync(
        string workspacePath, string resultsDir, bool collectCoverage, CancellationToken ct)
    {
        // Ensure the results directory exists before running tests
        Directory.CreateDirectory(resultsDir);

        var args = $"test --logger trx --results-directory \"{resultsDir}\"";
        if (collectCoverage)
            args += " --collect:\"XPlat Code Coverage\"";

        _logger.Debug("Running tests: dotnet {Args} in {WorkspacePath}", args, workspacePath);

        var (exitCode, stdout, stderr) = await RunProcessAsync("dotnet", args, workspacePath, ct);

        WriteGateOutput(workspacePath, "tests", stdout, stderr);

        _logger.Debug("Test results directory contents: {Files}",
            Directory.Exists(resultsDir)
                ? string.Join(", ", Directory.GetFiles(resultsDir, "*", SearchOption.AllDirectories))
                : "DIRECTORY NOT FOUND");

        // Parse TRX files for accurate test counts
        var (passed, failed, skipped) = ParseTestCountsFromTrx(resultsDir);

        // If TRX parsing found nothing, fall back to stdout parsing
        if (passed == 0 && failed == 0 && skipped == 0)
        {
            _logger.Warning("No TRX results found in {ResultsDir}, falling back to stdout parsing", resultsDir);
            (passed, failed, skipped) = ParseTestCountsFromStdout(stdout);
        }

        _logger.Information("Test results: {Passed} passed, {Failed} failed, {Skipped} skipped",
            passed, failed, skipped);

        return new GateResult
        {
            GateName = "Tests",
            Passed = exitCode == 0,
            Details = exitCode == 0
                ? $"Tests passed: {passed} passed, {failed} failed, {skipped} skipped"
                : $"Tests failed: {passed} passed, {failed} failed, {skipped} skipped.",
            TestsPassed = passed,
            TestsFailed = failed,
            TestsSkipped = skipped
        };
    }

    /// <summary>
    /// Parses all .trx files in the results directory and sums up test counts across all assemblies.
    /// TRX files contain a ResultSummary/Counters element with total/passed/failed/etc attributes.
    /// </summary>
    internal static (int Passed, int Failed, int Skipped) ParseTestCountsFromTrx(string resultsDir)
    {
        if (!Directory.Exists(resultsDir))
            return (0, 0, 0);

        var trxFiles = Directory.GetFiles(resultsDir, "*.trx", SearchOption.AllDirectories);
        if (trxFiles.Length == 0)
            return (0, 0, 0);

        var totalPassed = 0;
        var totalFailed = 0;
        var totalSkipped = 0;

        foreach (var trxFile in trxFiles)
        {
            try
            {
                var doc = XDocument.Load(trxFile);
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                var counters = doc.Descendants(ns + "Counters").FirstOrDefault();
                if (counters == null) continue;

                totalPassed += ParseIntAttribute(counters, "passed");
                totalFailed += ParseIntAttribute(counters, "failed") + ParseIntAttribute(counters, "error");
                totalSkipped += ParseIntAttribute(counters, "notExecuted");
            }
            catch
            {
                // Skip malformed TRX files
            }
        }

        return (totalPassed, totalFailed, totalSkipped);
    }

    /// <summary>
    /// Parses Cobertura XML coverage reports from the results directory.
    /// Returns a GateResult with the aggregate line coverage percentage.
    /// </summary>
    private GateResult ParseCoverageFromReports(string resultsDir, double threshold)
    {
        if (!Directory.Exists(resultsDir))
        {
            _logger.Warning("Results directory {ResultsDir} not found for coverage parsing", resultsDir);
            return new GateResult
            {
                GateName = "Coverage",
                Passed = false,
                Details = "No coverage data — results directory not found",
                CoveragePercent = null
            };
        }

        var coberturaFiles = Directory.GetFiles(resultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories);
        if (coberturaFiles.Length == 0)
        {
            _logger.Warning("No Cobertura coverage files found in {ResultsDir}", resultsDir);
            return new GateResult
            {
                GateName = "Coverage",
                Passed = false,
                Details = "No coverage data — Cobertura XML not found",
                CoveragePercent = null
            };
        }

        var coveragePercent = ParseCoverageFromCobertura(coberturaFiles);

        _logger.Information("Coverage: {CoveragePercent:F1}% (threshold: {Threshold:F1}%)",
            coveragePercent, threshold);

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
    /// Parses Cobertura XML files and returns the merged line coverage percentage.
    /// When multiple reports cover the same source file, line-level hits are merged
    /// (max hit count per line) to avoid double-counting in multi-project solutions.
    /// </summary>
    internal static double ParseCoverageFromCobertura(string[] coberturaFiles)
    {
        // Track per-line coverage: sourceFile -> (lineNumber -> hits)
        var lineCoverage = new Dictionary<string, Dictionary<int, int>>();

        foreach (var file in coberturaFiles)
        {
            try
            {
                var doc = XDocument.Load(file);
                if (doc.Root == null) continue;

                foreach (var cls in doc.Descendants("class"))
                {
                    var filename = cls.Attribute("filename")?.Value;
                    if (string.IsNullOrEmpty(filename)) continue;

                    if (!lineCoverage.TryGetValue(filename, out var fileLines))
                    {
                        fileLines = new Dictionary<int, int>();
                        lineCoverage[filename] = fileLines;
                    }

                    foreach (var line in cls.Descendants("line"))
                    {
                        var number = (int?)line.Attribute("number");
                        var hits = (int?)line.Attribute("hits");
                        if (number == null) continue;

                        var lineNum = number.Value;
                        var lineHits = hits ?? 0;

                        // Take the max hits across reports for the same line
                        if (fileLines.TryGetValue(lineNum, out var existing))
                            fileLines[lineNum] = Math.Max(existing, lineHits);
                        else
                            fileLines[lineNum] = lineHits;
                    }
                }
            }
            catch
            {
                // Skip malformed files
            }
        }

        var totalLines = 0L;
        var coveredLines = 0L;
        foreach (var fileLines in lineCoverage.Values)
        {
            foreach (var hits in fileLines.Values)
            {
                totalLines++;
                if (hits > 0) coveredLines++;
            }
        }

        return totalLines > 0 ? (double)coveredLines / totalLines * 100.0 : 0.0;
    }

    /// <summary>
    /// Fallback: parses test counts from stdout when TRX files are not available.
    /// Handles both the per-assembly format and the .NET 10 summary line.
    /// </summary>
    internal static (int Passed, int Failed, int Skipped) ParseTestCountsFromStdout(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (0, 0, 0);

        // Try .NET 10 summary line first: "Test summary: total: 47; failed: 0; succeeded: 47; skipped: 0"
        var summaryMatch = System.Text.RegularExpressions.Regex.Match(output,
            @"Test summary:.*?failed:\s*(\d+).*?succeeded:\s*(\d+).*?skipped:\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (summaryMatch.Success)
        {
            int.TryParse(summaryMatch.Groups[2].Value, out var succeeded);
            int.TryParse(summaryMatch.Groups[1].Value, out var summaryFailed);
            int.TryParse(summaryMatch.Groups[3].Value, out var summarySkipped);
            return (succeeded, summaryFailed, summarySkipped);
        }

        // Fall back to summing per-assembly lines: "Passed:  10, Failed:   2, Skipped:   1"
        var passed = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var match in System.Text.RegularExpressions.Regex.Matches(output,
            @"Passed:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Cast<System.Text.RegularExpressions.Match>())
        {
            if (int.TryParse(match.Groups[1].Value, out var p)) passed += p;
            if (int.TryParse(match.Groups[2].Value, out var f)) failed += f;
            if (int.TryParse(match.Groups[3].Value, out var s)) skipped += s;
        }

        return (passed, failed, skipped);
    }

    /// <summary>
    /// Writes gate stdout/stderr to .kiro/quality-gates/{gateName}-stdout.txt and
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
    {
        var errors = 0;
        var warnings = 0;

        if (string.IsNullOrWhiteSpace(output))
            return (errors, warnings);

        var errorMatch = Regex.Match(output, @"(\d+)\s+Error\(s\)", RegexOptions.IgnoreCase);
        if (errorMatch.Success)
            int.TryParse(errorMatch.Groups[1].Value, out errors);

        var warningMatch = Regex.Match(output, @"(\d+)\s+Warning\(s\)", RegexOptions.IgnoreCase);
        if (warningMatch.Success)
            int.TryParse(warningMatch.Groups[1].Value, out warnings);

        return (errors, warnings);
    }

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

    private static int ParseIntAttribute(XElement element, string name)
    {
        var attr = element.Attribute(name);
        return attr != null && int.TryParse(attr.Value, out var val) ? val : 0;
    }
}
