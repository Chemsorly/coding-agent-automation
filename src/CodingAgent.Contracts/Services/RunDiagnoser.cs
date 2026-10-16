using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Pure, deterministic rule-based diagnosis of a failed pipeline run.
/// No I/O, no clock, no logging — all rules are computed from the run summary and recent history.
/// Diagnoses are computed on page render and are not stored.
/// </summary>
public static class RunDiagnoser
{
    private const int EvidenceMaxEntries = 3;
    private const int EvidenceMaxChars = 300;
    private static readonly TimeSpan PlatformIncidentWindow = TimeSpan.FromMinutes(10);
    private const int PlatformIncidentMinOtherRuns = 3;
    private const int RepoGateMinOtherRuns = 2;

    /// <summary>
    /// Diagnoses a failed run according to the fixed rule set.
    /// Returns <see langword="null"/> when the run has not failed or was rejected by the analysis gate.
    /// </summary>
    /// <param name="run">The run to diagnose.</param>
    /// <param name="recentRuns">
    /// Recent runs to use as context (e.g. the last 100 failed runs loaded alongside this one).
    /// Null is treated as empty. The run itself is excluded from consideration even if present.
    /// </param>
    public static RunDiagnosis? Diagnose(
        PipelineRunSummary run,
        IReadOnlyList<PipelineRunSummary>? recentRuns = null)
    {
        // TODO: [WARNING] ArgumentNullException.ThrowIfNull(run) is missing. Passing null throws
        // NullReferenceException at the first member access on run, giving the caller a misleading
        // error message. Add ArgumentNullException.ThrowIfNull(run); as the first line of this method.
        if (run.FinalStep != PipelineStep.Failed)
            return null;
        if (run.FailureCategory == FailureReason.GateRejected)
            return null;

        var others = (recentRuns ?? [])
            .Where(r => r.RunId != run.RunId)
            .ToList();

        // Rule 1: Several runs failed at the same time (platform incident)
        if (IsPlatformCategory(run.FailureCategory) && run.CompletedAtOffset is { } runCompleted)
        {
            var platformOthers = others
                .Where(r => r.FinalStep == PipelineStep.Failed
                    && IsPlatformCategory(r.FailureCategory)
                    && r.IssueIdentifier != run.IssueIdentifier
                    && r.CompletedAtOffset is { } rc
                    && Abs(rc - runCompleted) <= PlatformIncidentWindow)
                .ToList();

            if (platformOthers.Count >= PlatformIncidentMinOtherRuns)
            {
                var n = platformOthers.Count;
                return new RunDiagnosis(
                    RunDiagnosisVerdict.Transient,
                    "Several runs failed at the same time",
                    $"{n} other runs failed within 10 minutes of this one. That points to a platform problem (API outage, node drain, cluster restart), not to this issue.",
                    "Re-dispatch the run once the platform is healthy.",
                    BuildEvidence(run, null, null));
            }
        }

        // Rule 2: OOM kill
        if (run.FailureCategory == FailureReason.ExitCodeFailure
            && ContainsOom(run.FailureReason))
        {
            return new RunDiagnosis(
                RunDiagnosisVerdict.Configuration,
                "The agent ran out of memory",
                "Kubernetes killed the agent container because it used more memory than its limit.",
                "Raise the memory limit of the agent's job template (jobTemplates in the Helm values), or split the issue into smaller ones.",
                BuildEvidence(run, null, null));
        }

        // Rule 3: Token refresh failure
        if (run.FailureCategory == FailureReason.TokenRefreshFailure)
        {
            return new RunDiagnosis(
                RunDiagnosisVerdict.Configuration,
                "The agent lost access to the providers",
                "The agent could not refresh its token for the issue tracker or the repository.",
                "Check the run's providers under Settings → Providers, for example an expired token or an app missing permissions.",
                BuildEvidence(run, null, null));
        }

        // Rule 4: Timeout
        if (run.FailureCategory == FailureReason.Timeout)
        {
            var stepName = run.LastActiveStep?.ToDisplayName();
            var explanation = stepName is not null
                ? $"The run was stopped during \"{stepName}\"."
                : "The run was stopped before it finished.";
            return new RunDiagnosis(
                RunDiagnosisVerdict.Configuration,
                "The run hit the agent timeout",
                explanation,
                "Raise AgentTimeout for the project if the issue needs more time, or split the issue.",
                BuildEvidence(run, null, null));
        }

        // Rule 5: Infrastructure failure
        if (run.FailureCategory == FailureReason.InfrastructureFailure)
        {
            return new RunDiagnosis(
                RunDiagnosisVerdict.Transient,
                "The platform could not run the job",
                "The job did not complete for a reason outside the agent: scheduling, image pull, no free credential volume, or a lost connection.",
                "Re-dispatch the run. If it happens again, check the job's events in the cluster and the credential volumes on the Fleet page.",
                BuildEvidence(run, null, null));
        }

        // Rule 6: Exit code failure (non-OOM)
        if (run.FailureCategory == FailureReason.ExitCodeFailure)
        {
            return new RunDiagnosis(
                RunDiagnosisVerdict.Agent,
                "The agent process crashed",
                "The agent container exited with an error.",
                "Re-dispatch the run. If it crashes again, the agent pod log on this page shows where.",
                BuildEvidence(run, lastPodOrOutputLine: true, gateNames: null));
        }

        // Rules 7 and 8: Quality gate condition
        var isGateCondition = run.FailureCategory == FailureReason.QualityGateExhausted
            || (run.QualityGateOutcomes?.Any(g => !g.Passed) ?? false);

        if (isGateCondition)
        {
            var failedGate = run.QualityGateOutcomes?.FirstOrDefault(g => !g.Passed);
            var gateName = failedGate?.GateName ?? "a quality gate";
            var failedGateNames = run.QualityGateOutcomes?
                .Where(g => !g.Passed)
                .Select(g => g.GateName)
                .ToList();

            // Cross-run condition for rule 7
            var crossRunCount = 0;
            if (run.RepoProviderConfigId is not null)
            {
                crossRunCount = others
                    .Where(r => r.FinalStep == PipelineStep.Failed
                        && r.RepoProviderConfigId == run.RepoProviderConfigId
                        && r.StartedAtOffset >= run.StartedAtOffset.AddHours(-24)
                        && r.StartedAtOffset <= run.StartedAtOffset
                        && (r.QualityGateOutcomes?.Any(g => !g.Passed && g.GateName == gateName) ?? false))
                    .Count();
            }

            if (crossRunCount >= RepoGateMinOtherRuns)
            {
                // Rule 7: Repository problem
                return new RunDiagnosis(
                    RunDiagnosisVerdict.Repository,
                    $"Quality gate \"{gateName}\" fails across runs",
                    $"The gate also failed in {crossRunCount} other runs of this repository in the last 24 hours, so the cause is probably in the repository (broken base branch, flaky test), not in this change.",
                    $"Check whether \"{gateName}\" passes on the base branch, and fix or quarantine the failing check.",
                    BuildEvidence(run, lastPodOrOutputLine: false, gateNames: failedGateNames));
            }
            else
            {
                // Rule 8: Agent could not fix the gate
                return new RunDiagnosis(
                    RunDiagnosisVerdict.Agent,
                    $"The agent could not make quality gate \"{gateName}\" pass",
                    $"\"{gateName}\" still failed after {run.RetryCount} retries.",
                    "Review the draft pull request, or refine the issue and queue it again.",
                    BuildEvidence(run, lastPodOrOutputLine: false, gateNames: failedGateNames));
            }
        }

        // Rule 9: Unknown
        {
            var explanation = !string.IsNullOrEmpty(run.FailureReason)
                ? run.FailureReason
                : "The run failed without a recorded reason.";
            return new RunDiagnosis(
                RunDiagnosisVerdict.Unknown,
                "No rule matched this failure",
                explanation,
                "Read the agent output and the agent pod log on this page.",
                BuildEvidence(run, lastPodOrOutputLine: true, gateNames: null));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsPlatformCategory(FailureReason? category) =>
        category is FailureReason.InfrastructureFailure
            or FailureReason.ExitCodeFailure
            or FailureReason.Timeout
            or FailureReason.TokenRefreshFailure;

    private static bool ContainsOom(string? reason) =>
        reason is not null
        && (reason.Contains("OOMKilled", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("exit code 137", StringComparison.OrdinalIgnoreCase));

    private static TimeSpan Abs(TimeSpan ts) => ts < TimeSpan.Zero ? -ts : ts;

    /// <summary>
    /// Builds the evidence list (at most 3 entries, each truncated to 300 chars).
    /// Evidence order:
    ///   1. run.FailureReason (always, when non-empty)
    ///   2. When lastPodOrOutputLine is true: last non-empty PodLogTail line, else last non-empty OutputTail line
    ///      When lastPodOrOutputLine is false: omitted
    ///   3. When gateNames is non-null and non-empty: "Failed gates: {names}"
    /// </summary>
    private static IReadOnlyList<string> BuildEvidence(
        PipelineRunSummary run,
        bool? lastPodOrOutputLine,
        IReadOnlyList<string>? gateNames)
    {
        var entries = new List<string>(EvidenceMaxEntries);

        // Entry 1: failure reason
        if (!string.IsNullOrEmpty(run.FailureReason))
            entries.Add(Truncate(run.FailureReason));

        // Entry 2: last pod/output log line (only for rules 6 and 9)
        if (lastPodOrOutputLine == true && entries.Count < EvidenceMaxEntries)
        {
            var tailLine = LastNonEmpty(run.PodLogTail) ?? LastNonEmpty(run.OutputTail);
            if (tailLine is not null)
                entries.Add(Truncate(tailLine));
        }

        // Entry 3: failed gate names (only for rules 7 and 8)
        if (gateNames is { Count: > 0 } && entries.Count < EvidenceMaxEntries)
        {
            var joined = "Failed gates: " + string.Join(", ", gateNames);
            entries.Add(Truncate(joined));
        }

        return entries;
    }

    private static string? LastNonEmpty(IReadOnlyList<string>? lines)
    {
        if (lines is null) return null;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
                return lines[i];
        }
        return null;
    }

    private static string Truncate(string s) =>
        s.Length <= EvidenceMaxChars ? s : s[..EvidenceMaxChars];
}
