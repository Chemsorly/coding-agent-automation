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

    private const string GateStatusPassed = "PASSED";
    private const string GateStatusFailed = "FAILED";

    internal static string FormatGateLogValue(GateResult? gate) =>
        gate is null ? "N/A" : gate.Passed.ToString();

    private static string BuildQualityGateErrorSummary(QualityGateReport report)
    {
        var errors = new List<string>();
        if (!report.Compilation.Passed)
            errors.Add($"Compilation: {report.Compilation.Details}");
        // TODO [WARNING]: report.Tests is accessed without a null-conditional here. A QGC configured
        // with only a BuildCommand and no TestCommand produces a QualityGateReport where Tests is null,
        // causing a NullReferenceException before BuildQualityGateRetryPrompt is even reached.
        // Fix: guard with `if (report.Tests is { Passed: false })` consistent with SecurityScan/ExternalCi.
        // See review finding: DotNetSpecialist WARNING — QualityGateExecutor.cs:80
        if (!report.Tests.Passed)
            errors.Add($"Tests: {report.Tests.Details}");
        if (report.SecurityScan is { Passed: false })
            errors.Add($"Security: {report.SecurityScan.Details}");
        if (report.ExternalCi is { Passed: false })
            errors.Add($"External CI: {report.ExternalCi.Details}");
        return string.Join(Environment.NewLine, errors);
    }

    internal static string BuildQualityGateRetryPrompt(QualityGateReport report, int attempt, int maxRetries, bool hasQualityGateOutput)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Quality gates failed (attempt {attempt}/{maxRetries}):");
        sb.AppendLine($"- Compilation: {(report.Compilation.Passed ? GateStatusPassed : GateStatusFailed)} ({report.Compilation.Details})");
        // TODO [WARNING]: report.Tests is dereferenced without a null-conditional. A QGC configured
        // with only a BuildCommand and no TestCommand produces a report where Tests is null, causing
        // a NullReferenceException here. The priorRetryErrors overload correctly uses report.Tests?.Passed
        // and report.Tests?.Details. Apply the same null-conditional pattern here for consistency.
        // See review finding: DotNetSpecialist WARNING — QualityGateExecutor.cs:100
        sb.AppendLine($"- Tests: {(report.Tests.Passed ? GateStatusPassed : GateStatusFailed)} ({report.Tests.Details})");
        if (report.SecurityScan != null)
            sb.AppendLine($"- Security: {(report.SecurityScan.Passed ? GateStatusPassed : GateStatusFailed)} ({report.SecurityScan.Details})");
        if (report.ExternalCi != null)
            sb.AppendLine($"- External CI: {(report.ExternalCi.Passed ? GateStatusPassed : GateStatusFailed)} ({report.ExternalCi.Details})");
        sb.AppendLine();
        if (hasQualityGateOutput)
        {
            sb.AppendLine($"Diagnostic output has been written to `{AgentWorkspacePaths.QualityGatesOutputDirectory}/`.");
            sb.AppendLine("List the files there and read the relevant ones.");
        }
        else
        {
            sb.AppendLine("No diagnostic output files were produced — the test process likely terminated abnormally before writing output.");
            sb.AppendLine($"Check `{AgentWorkspacePaths.FullDiffFilePath}` for your changes and run the failing test command directly to diagnose.");
        }
        sb.AppendLine();
        sb.AppendLine("Before fixing, reflect:");
        sb.AppendLine("1. **What specific code change caused this failure?** (identify the exact lines)");
        sb.AppendLine("2. **Why did you make that change?** (what was the intent)");
        sb.AppendLine("3. **What is the minimal fix** that addresses the failure without reverting the intended behavior?");
        sb.AppendLine();
        sb.Append("Apply the targeted fix, then verify by running the failing command again.");
        return sb.ToString();
    }

    /// <summary>
    /// Overload that accepts a snapshot of prior retry error summaries and derives
    /// <c>hasQualityGateOutput</c> from the report's infrastructure-failure classification.
    /// When <paramref name="priorRetryErrors"/> contains more than one entry, a "Prior attempt
    /// failures" history section is appended so the agent can detect recurring patterns.
    /// When all prior error entries are identical, an additional transient infrastructure failure
    /// warning is emitted to guide the agent away from pointless code fixes.
    /// </summary>
    /// <param name="report">The current quality gate report.</param>
    /// <param name="attempt">Current retry attempt number (1-based).</param>
    /// <param name="maxRetries">Maximum allowed retries.</param>
    /// <param name="priorRetryErrors">
    /// Snapshot of all retry error summaries accumulated so far (including the current attempt's
    /// summary which was enqueued before this call). Null or a single-entry list suppresses
    /// the history section — there is no useful history to show on the first attempt.
    /// </param>
    internal static string BuildQualityGateRetryPrompt(
        QualityGateReport report, int attempt, int maxRetries,
        IReadOnlyList<string>? priorRetryErrors)
    {
        // Derive hasQualityGateOutput from the report: an infrastructure failure means no
        // output was written (pipe was empty when process was killed), so we should not claim
        // diagnostic files exist. For all other failures, assume output was written.
        // Use null-conditional on report.Tests throughout: required on the model but may be null
        // when constructed outside BuildAggregateReport (e.g., legacy payloads, test helpers).
        // TODO [WARNING]: There is no test covering this overload with report.Tests == null (i.e. a run
        // where no QGC configures a test gate). The null-conditional guards below prevent a NRE, but
        // the resulting prompt would render "- Tests: FAILED ()" which is misleading. Add a test with
        // a report where Tests is null to verify graceful handling.
        // See review finding: TestQualityReviewer WARNING — QualityGateExecutor.cs
        var hasQualityGateOutput = !(report.QgcResults.Any(r => r.Tests?.IsInfrastructureFailure == true)
            || report.Tests?.IsInfrastructureFailure == true);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Quality gates failed (attempt {attempt}/{maxRetries}):");
        sb.AppendLine($"- Compilation: {(report.Compilation.Passed ? GateStatusPassed : GateStatusFailed)} ({report.Compilation.Details})");
        sb.AppendLine($"- Tests: {(report.Tests?.Passed == true ? GateStatusPassed : GateStatusFailed)} ({report.Tests?.Details})");
        if (report.SecurityScan != null)
            sb.AppendLine($"- Security: {(report.SecurityScan.Passed ? GateStatusPassed : GateStatusFailed)} ({report.SecurityScan.Details})");
        if (report.ExternalCi != null)
            sb.AppendLine($"- External CI: {(report.ExternalCi.Passed ? GateStatusPassed : GateStatusFailed)} ({report.ExternalCi.Details})");
        sb.AppendLine();
        if (hasQualityGateOutput)
        {
            sb.AppendLine($"Diagnostic output has been written to `{AgentWorkspacePaths.QualityGatesOutputDirectory}/`.");
            sb.AppendLine("List the files there and read the relevant ones.");
        }
        else
        {
            sb.AppendLine("No diagnostic output files were produced — the test process likely terminated abnormally before writing output.");
            sb.AppendLine($"Check `{AgentWorkspacePaths.FullDiffFilePath}` for your changes and run the failing test command directly to diagnose.");
        }

        // Append prior-attempt history when there is more than one entry. On the first retry
        // there is exactly one entry (enqueued just before this call), so the guard `Count > 1`
        // correctly suppresses the section on attempt 1.
        if (priorRetryErrors is { Count: > 1 })
        {
            sb.AppendLine();
            sb.AppendLine("**Prior attempt failures** (most recent last):");
            var recentErrors = priorRetryErrors.TakeLast(5).ToList();
            var startAttempt = attempt - recentErrors.Count;
            for (var i = 0; i < recentErrors.Count; i++)
                sb.AppendLine($"  Attempt {startAttempt + i + 1}: {recentErrors[i]}");

            // All entries identical → likely a transient infrastructure issue, not a code bug.
            var allIdentical = priorRetryErrors.Distinct().Count() == 1;
            if (allIdentical)
                sb.AppendLine("⚠️ All prior attempts produced identical failures — this is likely a transient infrastructure failure (e.g. OOM, flaky test environment). Consider whether a code fix is appropriate before retrying.");
        }

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
