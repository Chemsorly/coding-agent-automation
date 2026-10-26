using System.Text;
using System.Text.RegularExpressions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Agent.Executors;

/// <summary>
/// Composes GitHub-flavored Markdown issue bodies for refactoring proposals.
/// Extracted from <see cref="RefactoringExecutor"/> so that the two axes of change —
/// scan orchestration and issue-body rendering — evolve independently.
/// </summary>
internal static partial class RefactoringIssueBodyFormatter
{
    /// <summary>
    /// The footer sentence every issue of this scan carries (see <see cref="FormatIssueBody"/>). It tells the
    /// scan's issues apart from other <c>agent:generated</c> issues, such as decomposition sub-issues,
    /// without a label of their own; issues filed before this constant existed carry it too.
    /// </summary>
    internal const string GeneratedIssueFooter = RefactoringScanIssues.Footer;

    /// <summary>
    /// Formats the issue body from a refactoring proposal: the problem (<see cref="RefactoringProposal.Rationale"/>),
    /// the change (<see cref="RefactoringProposal.Description"/>), the files and scope, the evidence, and the
    /// acceptance criteria. <paramref name="commitSha"/> names the commit the analysis ran against, which the
    /// evidence line numbers refer to.
    /// </summary>
    internal static string FormatIssueBody(RefactoringProposal proposal, string? commitSha = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Problem");
        sb.AppendLine();
        sb.AppendLine(SanitizeMarkdown(proposal.Rationale));
        sb.AppendLine();

        AppendMetadataLine(sb, proposal);

        AppendBeforeYouStartSection(sb, proposal, commitSha);

        sb.AppendLine("## Suggested Approach");
        sb.AppendLine();
        sb.AppendLine(SanitizeMarkdown(proposal.Description));
        sb.AppendLine();

        if (proposal.Prerequisites is { Count: > 0 })
        {
            sb.AppendLine("## Prerequisites");
            sb.AppendLine();
            foreach (var prereq in proposal.Prerequisites.Where(p => p is not null))
                sb.AppendLine($"- {SanitizePrerequisite(prereq)}");
            sb.AppendLine();
        }

        sb.AppendLine("## Affected Components");
        sb.AppendLine();
        foreach (var file in proposal.AffectedFiles.Where(f => f is not null))
            sb.AppendLine($"- {CodeSpan(file)}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(proposal.ScopeQuery))
        {
            sb.AppendLine("## Scope");
            sb.AppendLine();
            sb.AppendLine("Every match of this search is in scope, unless the suggested approach excludes it:");
            sb.AppendLine();
            AppendCodeBlock(sb, proposal.ScopeQuery, "sh");
            sb.AppendLine();
        }

        AppendEvidenceSection(sb, proposal);

        sb.AppendLine("## Acceptance Criteria");
        sb.AppendLine();

        AppendAcceptanceCriteria(sb, proposal);

        sb.AppendLine();
        sb.AppendLine("---");
        sb.Append('*').Append(GeneratedIssueFooter);
        sb.Append(commitSha is null
            ? ".*"
            : $" at commit `{commitSha[..Math.Min(12, commitSha.Length)]}`. Line numbers refer to that commit.*");

        return sb.ToString();
    }

    private static void AppendMetadataLine(StringBuilder sb, RefactoringProposal proposal)
    {
        var parts = new List<string>();
        if (proposal.Category is not null)
            parts.Add($"**Category:** {SanitizeMarkdown(proposal.Category)}");
        if (proposal.EstimatedEffort is not null)
            parts.Add($"**Effort:** {SanitizeMarkdown(proposal.EstimatedEffort)}");
        if (proposal.RiskLevel is not null)
            parts.Add($"**Risk:** {SanitizeMarkdown(proposal.RiskLevel)}");
        if (proposal.Technique is not null)
            parts.Add($"**Technique:** {SanitizeMarkdown(proposal.Technique)}");

        if (parts.Count == 0)
            return;

        sb.AppendLine(string.Join(" | ", parts));
        sb.AppendLine();
    }

    private static void AppendBeforeYouStartSection(StringBuilder sb, RefactoringProposal proposal, string? commitSha)
    {
        var hasScope = !string.IsNullOrWhiteSpace(proposal.ScopeQuery);
        var hasEvidence = !string.IsNullOrWhiteSpace(proposal.Evidence);
        if (!hasScope && !hasEvidence)
            return;

        sb.AppendLine("## Before You Start");
        sb.AppendLine();

        var intro = commitSha is null
            ? "This issue was written against an earlier commit. Check that the problem still exists before you change anything:"
            : $"This issue was written against commit `{commitSha[..Math.Min(12, commitSha.Length)]}`. Check that the problem still exists before you change anything:";
        sb.AppendLine(intro);
        sb.AppendLine();

        if (hasScope)
            sb.AppendLine("- Run the search under **Scope**. If it finds none of the instances this issue describes, the problem is already fixed.");
        if (hasEvidence)
            sb.AppendLine("- Look for the code quoted under **Evidence**. If it is gone and the problem it shows no longer exists, the problem is already fixed.");

        sb.AppendLine();
        sb.AppendLine("If the problem is already fixed, report this issue as `wont_do` and say what you checked. Do not make other changes instead, such as adding tests.");
        sb.AppendLine();
    }

    private static void AppendEvidenceSection(StringBuilder sb, RefactoringProposal proposal)
    {
        var hasEvidence = !string.IsNullOrWhiteSpace(proposal.Evidence);
        var sources = proposal.EvidenceSources?.Where(s => s is not null).ToList() ?? [];
        if (!hasEvidence && sources.Count == 0)
            return;

        sb.AppendLine("## Evidence");
        sb.AppendLine();
        if (hasEvidence)
        {
            AppendCodeBlock(sb, proposal.Evidence!, language: null);
            sb.AppendLine();
        }

        foreach (var source in sources)
            sb.AppendLine($"- {CodeSpan(source)}");
        if (sources.Count > 0)
            sb.AppendLine();
    }

    private static void AppendAcceptanceCriteria(StringBuilder sb, RefactoringProposal proposal)
    {
        var criteria = proposal.AcceptanceCriteria;
        if (criteria is { Count: > 0 })
        {
            foreach (var criterion in criteria.Where(c => c is not null))
                sb.AppendLine($"- [ ] {SanitizeMarkdown(criterion)}");
        }
        else if (string.Equals(proposal.Category?.Trim(), RefactoringCategories.Bug, StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("- [ ] A test reproduces the failure described under Problem and passes after the fix");
            sb.AppendLine("- [ ] Behavior outside the described failure is unchanged");
        }
        else
        {
            sb.AppendLine("- [ ] Refactoring applied without changing observable behavior");
            sb.AppendLine("- [ ] All existing tests continue to pass");
        }
    }

    /// <summary>
    /// Renders <paramref name="text"/> as an inline code span. Markdown and HTML are not interpreted inside
    /// a code span, so only backticks, which would end it early, need replacing.
    /// </summary>
    private static string CodeSpan(string text) => $"`{text.Replace('`', '\'')}`";

    /// <summary>
    /// Renders <paramref name="text"/> verbatim in a fenced code block. The fence is longer than any backtick
    /// run in the text, so the text cannot close it; markdown, HTML and mentions are not interpreted inside.
    /// </summary>
    private static void AppendCodeBlock(StringBuilder sb, string text, string? language)
    {
        var longestBacktickRun = 0;
        var currentRun = 0;
        foreach (var c in text)
        {
            currentRun = c == '`' ? currentRun + 1 : 0;
            longestBacktickRun = Math.Max(longestBacktickRun, currentRun);
        }

        var fence = new string('`', Math.Max(3, longestBacktickRun + 1));
        sb.AppendLine(fence + language);
        sb.AppendLine(text.TrimEnd());
        sb.AppendLine(fence);
    }

    /// <summary>
    /// Sanitizes the proposal title for use in GitHub issue titles.
    /// Truncates to 200 chars and strips newlines.
    /// </summary>
    internal static string SanitizeTitle(string title) => TextSanitizer.SanitizeTitle(title);

    /// <summary>
    /// Escapes markdown-sensitive characters to prevent injection in GitHub issues.
    /// Delegates to <see cref="TextSanitizer.SanitizeMarkdown"/>.
    /// </summary>
    private static string SanitizeMarkdown(string value) => TextSanitizer.SanitizeMarkdown(value);

    /// <summary>
    /// Sanitizes a prerequisite like <see cref="SanitizeMarkdown"/> and wraps bare <c>#N</c> in a
    /// code span. Agents write "proposal #1", and GitHub would link that to unrelated issue 1.
    /// The lookbehind leaves "C# 12" and "F#8" alone (issue #1450).
    /// </summary>
    private static string SanitizePrerequisite(string value) =>
        HashNumberPattern().Replace(SanitizeMarkdown(value), "`#$1`");

    [GeneratedRegex(@"(?<![A-Za-z])#(\d+)")]
    private static partial Regex HashNumberPattern();

    /// <summary>
    /// Formats the refactoring run summary with issue count and identifiers.
    /// Distinguishes between "no proposals found" and "proposals found but issue creation failed".
    /// When <paramref name="firstFailureHint"/> is provided, it is included in the summary for
    /// both the all-failed (0/N) and partial-failure (k/N) cases. <paramref name="droppedCount"/> counts the
    /// proposals that failed validation; they are not part of <paramref name="proposalCount"/>.
    /// </summary>
    internal static string FormatRefactoringSummary(
        IReadOnlyList<CreatedIssueInfo> createdIssues, int proposalCount = 0, string? firstFailureHint = null, int droppedCount = 0)
    {
        var summary = FormatIssueCreationSummary(createdIssues, proposalCount, firstFailureHint);
        return droppedCount > 0
            ? $"{summary} ({droppedCount} proposal(s) dropped by validation)"
            : summary;
    }

    private static string FormatIssueCreationSummary(IReadOnlyList<CreatedIssueInfo> createdIssues, int proposalCount, string? firstFailureHint)
    {
        if (createdIssues.Count == 0 && proposalCount == 0)
            return "No refactoring opportunities identified";

        if (createdIssues.Count == 0 && proposalCount > 0)
        {
            var hint = firstFailureHint ?? "check logs for details";
            return $"Found {proposalCount} proposal(s) but failed to create issues ({hint})";
        }

        var identifiers = string.Join(", ", createdIssues.Select(i => $"#{i.Identifier}"));
        if (createdIssues.Count < proposalCount)
        {
            var partialHint = firstFailureHint is not null ? $" — first failure: {firstFailureHint}" : "";
            return $"Created {createdIssues.Count}/{proposalCount} refactoring issue(s): {identifiers} ({proposalCount - createdIssues.Count} failed{partialHint})";
        }

        return $"Created {createdIssues.Count} refactoring issue(s): {identifiers}";
    }
}
