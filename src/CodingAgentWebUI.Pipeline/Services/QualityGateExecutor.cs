using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using System.Diagnostics.Metrics;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Handles quality gate validation with retry logic and external CI integration.
/// Extracted from PipelineOrchestrationService.
/// Split into partial classes by concern for maintainability.
/// </summary>
public partial class QualityGateExecutor : IQualityGateExecutor
{
    private readonly IQualityGateValidator _qualityGateValidator;
    private readonly CiLogWriter _ciLogWriter;
    private readonly PullRequestOrchestrator _prOrchestrator;
    private readonly IPipelineRunHistoryService? _historyService;
    private readonly FeedbackService _feedbackService;
    private readonly Serilog.ILogger _logger;

    private readonly Histogram<double> _qualityGateDuration;
    private readonly Histogram<double> _postPrCiDuration;
    private readonly Counter<long> _qualityGateRetries;
    private readonly Counter<long> _qualityGateEvaluations;
    private readonly Histogram<double> _stepDuration;
    private readonly Counter<long> _stepCount;
    private readonly Histogram<double> _externalCiDuration;

    public QualityGateExecutor(
        IQualityGateValidator qualityGateValidator,
        PullRequestOrchestrator prOrchestrator,
        CiLogWriter ciLogWriter,
        FeedbackService feedbackService,
        Serilog.ILogger logger,
        IPipelineRunHistoryService? historyService = null,
        IMeterFactory? meterFactory = null)
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

        if (meterFactory is not null)
        {
            var meter = meterFactory.Create(new MeterOptions(PipelineTelemetry.SourceName));
            _qualityGateDuration = meter.CreateHistogram<double>("quality_gate.duration", "s", "Total time in quality gate phase");
            _postPrCiDuration = meter.CreateHistogram<double>("quality_gate.post_pr_ci.duration", "s", "Time waiting for post-PR CI to complete",
                advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [5, 10, 30, 60, 120, 300, 600, 1200, 1800, 3600] });
            _qualityGateRetries = meter.CreateCounter<long>("quality_gate.retries", "{retry}", "Quality gate retry attempts");
            _qualityGateEvaluations = meter.CreateCounter<long>("quality_gate.evaluations", "{evaluation}", "Individual gate evaluation events");
            _stepDuration = meter.CreateHistogram<double>("pipeline.step.duration", "s", "Duration of individual pipeline steps",
                advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [5, 15, 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600] });
            _stepCount = meter.CreateCounter<long>("pipeline.step.count", "{step}", "Pipeline step execution count");
            _externalCiDuration = meter.CreateHistogram<double>("quality_gate.external_ci.duration", "s", "Time waiting for external CI");
        }
        else
        {
            _qualityGateDuration = PipelineTelemetry.QualityGateDuration;
            _postPrCiDuration = PipelineTelemetry.PostPrCiDuration;
            _qualityGateRetries = PipelineTelemetry.QualityGateRetries;
            _qualityGateEvaluations = PipelineTelemetry.QualityGateEvaluations;
            _stepDuration = PipelineTelemetry.StepDuration;
            _stepCount = PipelineTelemetry.StepCount;
            _externalCiDuration = PipelineTelemetry.ExternalCiDuration;
        }
    }

    internal static string FormatGateLogValue(GateResult? gate) =>
        gate is null ? "N/A" : gate.Passed.ToString();

    private static string BuildQualityGateErrorSummary(QualityGateReport report)
    {
        var errors = new List<string>();
        if (!report.Compilation.Passed)
            errors.Add($"Compilation: {report.Compilation.Details}");
        if (!report.Tests.Passed)
            errors.Add($"Tests: {report.Tests.Details}");
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
