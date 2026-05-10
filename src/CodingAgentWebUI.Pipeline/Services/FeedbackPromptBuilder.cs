using System.Text;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Builds feedback-specific prompt sections for agent feedback collection.
/// Success feedback is appended to the existing reflection prompt.
/// Failure feedback is a standalone prompt sent as a dedicated agent call.
/// </summary>
public static class FeedbackPromptBuilder
{
    private const string JsonSchemaExample = """
        ```json
        {
          "harness": {
            "category": "short root-cause label (2-4 words, max 50 chars)",
            "stuckReason": "what blocked progress (required for failure, max 500 chars)",
            "missingContext": ["file or data that should have been provided upfront"],
            "missingCapabilities": ["tool or ability you wished you had"],
            "promptIssues": ["confusing or contradictory instruction from the pipeline"],
            "suggestions": ["concrete improvement to the harness"]
          },
          "issue": {
            "category": "short issue-quality label (2-4 words, max 50 chars)",
            "description": "what is wrong with the issue or repository (max 500 chars)",
            "affectedFiles": ["specific file paths where problems were found"],
            "humanActionNeeded": "what the issue author should do (max 500 chars)"
          }
        }
        ```
        """;

    /// <summary>
    /// Builds the feedback questions section to append to the reflection prompt (success path).
    /// Includes retry count, error summaries, elapsed time, and previously-used categories as grounding context.
    /// </summary>
    public static string BuildSuccessFeedbackSection(
        PipelineRun run,
        TimeSpan elapsed,
        IReadOnlyList<string> previousHarnessCategories,
        IReadOnlyList<string> previousIssueCategories)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(previousHarnessCategories);
        ArgumentNullException.ThrowIfNull(previousIssueCategories);

        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Feedback Collection");
        sb.AppendLine();
        sb.AppendLine("In addition to the reflection above, please provide structured feedback about this run.");
        sb.AppendLine("This helps the pipeline team identify improvement opportunities.");
        sb.AppendLine();

        // Grounding context: elapsed time
        sb.AppendLine("### Run Context");
        sb.AppendLine();
        sb.AppendLine($"- **Elapsed time:** {FormatElapsed(elapsed)}");
        sb.AppendLine($"- **Retry count:** {run.RetryCount}");

        // Grounding context: retry errors
        var retryErrors = run.RetryErrors.ToList();
        if (retryErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Errors encountered during retries:**");
            foreach (var error in retryErrors)
            {
                sb.AppendLine($"- {error}");
            }
        }

        sb.AppendLine();

        // Feedback questions with clear distinction
        sb.AppendLine("### Feedback Questions");
        sb.AppendLine();
        sb.AppendLine("Answer the following based on your experience during this run. Ground your answers in concrete evidence — reference specific file names, error messages, or tool names rather than making abstract statements.");
        sb.AppendLine();
        sb.AppendLine("**Harness Feedback** (things about the pipeline, tools, or prompts that could be improved):");
        sb.AppendLine("- What category best describes any friction you encountered? (short label, 2-4 words)");
        sb.AppendLine("- If retries occurred, what caused them?");
        sb.AppendLine("- Were there files, data, or context that should have been provided upfront?");
        sb.AppendLine("- Were there tools or capabilities you wished you had?");
        sb.AppendLine("- Were any pipeline instructions confusing, contradictory, or unhelpful?");
        sb.AppendLine("- What concrete improvements would you suggest for the harness?");
        sb.AppendLine();
        sb.AppendLine("**Issue Feedback** (things about the issue description or repository that were problematic):");
        sb.AppendLine("- If the issue or repository had problems, what category describes them? (short label, 2-4 words)");
        sb.AppendLine("- What specifically was wrong with the issue or repo?");
        sb.AppendLine("- Which files were affected?");
        sb.AppendLine("- What should the issue author do to fix the problem?");
        sb.AppendLine("- If the issue was well-written and the repo was clean, leave the issue section as null.");
        sb.AppendLine();

        // Previous categories for reuse
        AppendPreviousCategories(sb, previousHarnessCategories, previousIssueCategories);

        // JSON schema instruction
        sb.AppendLine("### Response Format");
        sb.AppendLine();
        sb.AppendLine("Produce a JSON block with the following structure. Reuse an existing category label if the root cause matches, or create a new short label (2-4 words) if it's genuinely novel.");
        sb.AppendLine();
        sb.AppendLine(JsonSchemaExample);

        return sb.ToString();
    }

    /// <summary>
    /// Builds the dedicated failure feedback prompt.
    /// Includes issue description, retry errors, latest QG report, retry count, and previously-used categories.
    /// </summary>
    public static string BuildFailureFeedbackPrompt(
        PipelineRun run,
        IssueDetail issue,
        QualityGateReport latestReport,
        IReadOnlyList<string> previousHarnessCategories,
        IReadOnlyList<string> previousIssueCategories)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(latestReport);
        ArgumentNullException.ThrowIfNull(previousHarnessCategories);
        ArgumentNullException.ThrowIfNull(previousIssueCategories);

        var sb = new StringBuilder();

        sb.AppendLine("# Pipeline Failure Feedback");
        sb.AppendLine();
        sb.AppendLine("The pipeline has exhausted its retry budget and quality gates still fail.");
        sb.AppendLine("Please provide structured feedback explaining what went wrong and what could be improved.");
        sb.AppendLine();

        // Original issue context
        sb.AppendLine("## Original Issue");
        sb.AppendLine();
        sb.AppendLine($"**Title:** {issue.Title}");
        sb.AppendLine();
        sb.AppendLine("**Description:**");
        sb.AppendLine(issue.Description);
        sb.AppendLine();

        // Retry context
        sb.AppendLine("## Retry Context");
        sb.AppendLine();
        sb.AppendLine($"- **Retry count:** {run.RetryCount}");

        var retryErrors = run.RetryErrors.ToList();
        if (retryErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Errors encountered during retries:**");
            foreach (var error in retryErrors)
            {
                sb.AppendLine($"- {error}");
            }
        }

        sb.AppendLine();

        // Latest quality gate report
        sb.AppendLine("## Latest Quality Gate Report");
        sb.AppendLine();
        AppendQualityGateReport(sb, latestReport);
        sb.AppendLine();

        // Feedback instructions
        sb.AppendLine("## Feedback Instructions");
        sb.AppendLine();
        sb.AppendLine("Based on the errors above and your experience during this run, provide structured feedback.");
        sb.AppendLine("Ground your answers in concrete evidence — reference specific file names, error messages, or tool names.");
        sb.AppendLine();
        sb.AppendLine("**You MUST explain the `stuckReason`:** What pipeline/tool limitation or issue problem blocked progress?");
        sb.AppendLine();
        sb.AppendLine("**Distinguish between:**");
        sb.AppendLine("- **Harness feedback** — things about the pipeline, tools, or prompts that the pipeline team can fix");
        sb.AppendLine("- **Issue feedback** — things about the issue description or repository that the issue author needs to fix");
        sb.AppendLine();
        sb.AppendLine("If the issue itself contributed to the failure (e.g., contradictory acceptance criteria, missing component, pre-existing bug), fill the `issue` section. Otherwise, set it to null.");
        sb.AppendLine();

        // Previous categories for reuse
        AppendPreviousCategories(sb, previousHarnessCategories, previousIssueCategories);

        // JSON schema instruction
        sb.AppendLine("## Response Format");
        sb.AppendLine();
        sb.AppendLine("Produce a JSON block with the following structure. The `stuckReason` field is required for failure feedback. Reuse an existing category label if the root cause matches, or create a new short label (2-4 words) if it's genuinely novel.");
        sb.AppendLine();
        sb.AppendLine(JsonSchemaExample);

        return sb.ToString();
    }

    private static void AppendPreviousCategories(
        StringBuilder sb,
        IReadOnlyList<string> harnessCategories,
        IReadOnlyList<string> issueCategories)
    {
        if (harnessCategories.Count == 0 && issueCategories.Count == 0)
            return;

        sb.AppendLine("### Previously Used Categories");
        sb.AppendLine();
        sb.AppendLine("Reuse an existing label if the root cause matches. Only create a new label if the situation is genuinely novel.");
        sb.AppendLine();

        if (harnessCategories.Count > 0)
        {
            sb.AppendLine("**Harness categories from recent runs:**");
            foreach (var category in harnessCategories)
            {
                sb.AppendLine($"- {category}");
            }
            sb.AppendLine();
        }

        if (issueCategories.Count > 0)
        {
            sb.AppendLine("**Issue categories from recent runs:**");
            foreach (var category in issueCategories)
            {
                sb.AppendLine($"- {category}");
            }
            sb.AppendLine();
        }
    }

    private static void AppendQualityGateReport(StringBuilder sb, QualityGateReport report)
    {
        sb.AppendLine($"- **Compilation:** {(report.Compilation.Passed ? "PASSED" : "FAILED")}");
        if (!string.IsNullOrEmpty(report.Compilation.Details))
            sb.AppendLine($"  - Details: {report.Compilation.Details}");

        sb.AppendLine($"- **Tests:** {(report.Tests.Passed ? "PASSED" : "FAILED")}");
        if (!string.IsNullOrEmpty(report.Tests.Details))
            sb.AppendLine($"  - Details: {report.Tests.Details}");
        if (report.Tests.TestsPassed.HasValue || report.Tests.TestsFailed.HasValue)
            sb.AppendLine($"  - Passed: {report.Tests.TestsPassed ?? 0}, Failed: {report.Tests.TestsFailed ?? 0}, Skipped: {report.Tests.TestsSkipped ?? 0}");

        if (report.Coverage is not null)
        {
            sb.AppendLine($"- **Coverage:** {(report.Coverage.Passed ? "PASSED" : "FAILED")}");
            if (report.Coverage.CoveragePercent.HasValue)
                sb.AppendLine($"  - Coverage: {report.Coverage.CoveragePercent:F1}%");
            if (!string.IsNullOrEmpty(report.Coverage.Details))
                sb.AppendLine($"  - Details: {report.Coverage.Details}");
        }

        if (report.SecurityScan is not null)
        {
            sb.AppendLine($"- **Security Scan:** {(report.SecurityScan.Passed ? "PASSED" : "FAILED")}");
            if (!string.IsNullOrEmpty(report.SecurityScan.Details))
                sb.AppendLine($"  - Details: {report.SecurityScan.Details}");
        }

        if (report.ExternalCi is not null)
        {
            sb.AppendLine($"- **External CI:** {(report.ExternalCi.Passed ? "PASSED" : "FAILED")}");
            if (!string.IsNullOrEmpty(report.ExternalCi.Details))
                sb.AppendLine($"  - Details: {report.ExternalCi.Details}");
        }

        if (report.QgcResults.Count > 0)
        {
            sb.AppendLine("- **QGC Results:**");
            foreach (var qgc in report.QgcResults)
            {
                sb.AppendLine($"  - {qgc.DisplayName}: {(qgc.Passed ? "PASSED" : "FAILED")}");
            }
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
            return $"{elapsed.Hours}h {elapsed.Minutes}m {elapsed.Seconds}s";
        if (elapsed.TotalMinutes >= 1)
            return $"{elapsed.Minutes}m {elapsed.Seconds}s";
        return $"{elapsed.Seconds}s";
    }
}
