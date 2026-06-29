using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Handles quality gate validation with retry logic and external CI integration.
/// Extracted from PipelineOrchestrationService.
/// Split into partial classes by concern for maintainability.
/// </summary>
internal partial class QualityGateExecutor : IQualityGateExecutor
{
    private readonly IQualityGateValidator _qualityGateValidator;
    private readonly CiLogWriter _ciLogWriter;
    private readonly PullRequestOrchestrator _prOrchestrator;
    private readonly IPipelineRunHistoryService? _historyService;
    private readonly FeedbackService _feedbackService;
    private readonly Serilog.ILogger _logger;

    public QualityGateExecutor(
        IQualityGateValidator qualityGateValidator,
        PullRequestOrchestrator prOrchestrator,
        CiLogWriter ciLogWriter,
        FeedbackService feedbackService,
        Serilog.ILogger logger,
        IPipelineRunHistoryService? historyService = null)
    {
        ArgumentNullException.ThrowIfNull(qualityGateValidator);
        ArgumentNullException.ThrowIfNull(prOrchestrator);
        ArgumentNullException.ThrowIfNull(ciLogWriter);
        ArgumentNullException.ThrowIfNull(feedbackService);
        ArgumentNullException.ThrowIfNull(logger);

        _qualityGateValidator = qualityGateValidator;
        _ciLogWriter = ciLogWriter;
        _prOrchestrator = prOrchestrator;
        _historyService = historyService;
        _feedbackService = feedbackService;
        _logger = logger;
    }

    internal static string FormatGateLogValue(GateResult? gate) =>
        gate is null ? "N/A" : gate.Passed.ToString();

    internal static string FormatCoverageLogValue(GateResult? gate) =>
        gate is null ? "N/A" : gate.CoveragePercent.HasValue
            ? $"{gate.Passed} ({gate.CoveragePercent.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}%)"
            : gate.Passed.ToString();

    private static string BuildQualityGateErrorSummary(QualityGateReport report)
    {
        var errors = new List<string>();
        if (!report.Compilation.Passed)
            errors.Add($"Compilation: {report.Compilation.Details}");
        if (!report.Tests.Passed)
            errors.Add($"Tests: {report.Tests.Details}");
        if (report.Coverage is { Passed: false })
            errors.Add($"Coverage: {report.Coverage.Details}");
        if (report.SecurityScan is { Passed: false })
            errors.Add($"Security: {report.SecurityScan.Details}");
        if (report.ExternalCi is { Passed: false })
            errors.Add($"External CI: {report.ExternalCi.Details}");
        return string.Join(Environment.NewLine, errors);
    }

    internal static string BuildQualityGateRetryPrompt(QualityGateReport report, int attempt, int maxRetries)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Quality gates failed (attempt {attempt}/{maxRetries}):");
        sb.AppendLine($"- Compilation: {(report.Compilation.Passed ? "PASSED" : "FAILED")} ({report.Compilation.Details})");
        sb.AppendLine($"- Tests: {(report.Tests.Passed ? "PASSED" : "FAILED")} ({report.Tests.Details})");
        if (report.Coverage != null)
            sb.AppendLine($"- Coverage: {(report.Coverage.Passed ? "PASSED" : "FAILED")} ({report.Coverage.Details})");
        if (report.SecurityScan != null)
            sb.AppendLine($"- Security: {(report.SecurityScan.Passed ? "PASSED" : "FAILED")} ({report.SecurityScan.Details})");
        if (report.ExternalCi != null)
            sb.AppendLine($"- External CI: {(report.ExternalCi.Passed ? "PASSED" : "FAILED")} ({report.ExternalCi.Details})");
        sb.AppendLine();
        sb.AppendLine($"Diagnostic output has been written to `{AgentWorkspacePaths.QualityGatesOutputDirectory}/`.");
        sb.AppendLine("List the files there and read the relevant ones.");
        sb.AppendLine();
        sb.AppendLine("Before fixing, reflect:");
        sb.AppendLine("1. **What specific code change caused this failure?** (identify the exact lines)");
        sb.AppendLine("2. **Why did you make that change?** (what was the intent)");
        sb.AppendLine("3. **What is the minimal fix** that addresses the failure without reverting the intended behavior?");
        sb.AppendLine();
        sb.Append("Apply the targeted fix, then verify by running the failing command again.");
        return sb.ToString();
    }
}
