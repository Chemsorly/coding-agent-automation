using System.Text;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services.Prompts;

/// <summary>
/// Builds the prompts of a triage run: the investigation, its adversarial review and the refinement. The
/// prompts are built in, like the decomposition and refactoring prompts.
/// </summary>
public static class TriagePromptBuilder
{
    /// <summary>
    /// Builds the investigation prompt.
    /// </summary>
    /// <param name="reportToTracker">
    /// True when the result is posted on a tracker issue (which may be public): log lines must not be pasted
    /// and other triages must not be quoted.
    /// </param>
    /// <param name="projectContext">The project's repositories; null when the run has only its own repository.</param>
    public static string BuildInvestigationPrompt(bool reportToTracker, DecompositionProjectContext? projectContext)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Triage: Find the Root Cause of a Reported Problem");
        sb.AppendLine();
        sb.AppendLine("Someone reported a problem with this system. Investigate it with every tool you have, find the root cause,");
        sb.AppendLine("and propose fix issues. Your result goes to a person who decides what to do next, so they must be able to");
        sb.AppendLine("check every claim you make and judge whether you looked in the right places.");
        sb.AppendLine();

        PromptBuilder.AppendSection(sb, "## Inputs", s =>
        {
            s.AppendLine($"- `{AgentWorkspacePaths.IssueContextFilePath}` — the report: what happened, what was expected, when and where, and any comments.");
            s.AppendLine($"- `{AgentWorkspacePaths.TriageContextFilePath}` — earlier attempts of this triage with the feedback or answers people gave, and other recent triages of the project.");
            s.AppendLine($"- `{AgentWorkspacePaths.OpenIssuesDirectory}/` — open issues of the project's repositories.");
            if (projectContext is not null)
                s.AppendLine($"- `{AgentWorkspacePaths.ProjectContextFilePath}` — the project's repositories. The workspace root is one of them; the others are read-only clones under `repos/`.");
            s.AppendLine("- Your MCP tools — for example logs, metrics, traces, code quality, documentation. List them before you start.");
            s.AppendLine($"- `{AgentWorkspacePaths.BrainContextFilePath}`, when present — project knowledge.");
        });

        PromptBuilder.AppendSection(sb, "## Method", s =>
        {
            s.AppendLine("1. **Restate the report.** What is the symptom, what was expected, when and where did it happen, which version was running. Note what is unknown.");
            s.AppendLine("2. **Take stock of your tools.** Which MCP servers and commands can give you evidence? Note any you would need but do not have.");
            s.AppendLine("3. **Check the cheap, high-signal sources first:** existing open issues and earlier triages (is this a duplicate?), and what changed around the time it started (commits, merged pull requests, deployments, configuration).");
            s.AppendLine("4. **Form at least three different hypotheses before you test any.** Agents tend to lock onto the first explanation; do not. Give each an id (H1, H2, …).");
            s.AppendLine("5. **Test each hypothesis** against logs, metrics, traces, code and documentation. Look for evidence against it as hard as for evidence for it. A coincidence in time is not a cause.");
            s.AppendLine("6. **Reproduce the problem if you can**, for example with a failing test in the workspace. Do not commit or push it.");
            s.AppendLine("7. **Write the causal chain** from the symptom back to the root cause. Every link needs evidence. Distinguish the trigger (what set it off) from the root cause (what made the system fail).");
            s.AppendLine("8. **Decide the verdict.** If you cannot tie the problem to one cause with evidence, say so and ask the questions that would let the next attempt decide — do not guess.");
            s.AppendLine("9. **Propose fixes** as complete issue drafts: the root fix, and where useful a mitigation and a prevention (a test, a check, an alert).");
        });

        PromptBuilder.AppendSection(sb, "## Rules", s =>
        {
            s.AppendLine("- **Record every check** in `investigated`, including the ones that found nothing. A person uses this list to judge whether you looked in the right places.");
            s.AppendLine("- **Every claim needs evidence** with its source: the tool and the query, command or file:line that produced it. No evidence, no claim.");
            s.AppendLine("- **Read-only outside the workspace.** Never write to external systems through MCP tools (no dashboards, alerts, incidents, tickets or comments). Never create branches, commits, pushes or pull requests.");
            s.AppendLine("- **No secrets or personal data** in your result: no tokens, passwords, e-mail addresses, customer names or payment data, even when a log line contains them.");
            if (reportToTracker)
            {
                s.AppendLine("- **Your result is posted on a tracker issue that may be public.** Do not paste log lines or query output: give the query and a link instead, and summarise what it showed. Do not quote other triages; name a duplicate only by its issue link.");
            }
            else
            {
                s.AppendLine("- Prefer queries and links over pasted log lines. Quote at most a few short lines when they are the decisive evidence.");
            }
            s.AppendLine($"- Propose at most **{TriageConstants.MaxDrafts}** drafts. Each must be completable by one agent in one run and must trace back to the root cause.");
            s.AppendLine("- Do not propose a draft that duplicates an open issue; mention the issue instead.");
            if (projectContext is not null)
                s.AppendLine("- Set each draft's `targetRepository` to the repository that must change, using its exact name from the project context.");
        });

        PromptBuilder.AppendSection(sb, "## Draft Body", s =>
        {
            s.AppendLine("Write each draft's `body` in Markdown with these sections:");
            s.AppendLine();
            s.AppendLine("- **## Problem** — what goes wrong and who is affected");
            s.AppendLine("- **## Root Cause** — the cause this draft addresses, with file:line");
            s.AppendLine("- **## Evidence** — the decisive evidence (queries, file:line, the reproduction)");
            s.AppendLine("- **## Before You Start** — how to check that the problem still exists; report `wont_do` if it no longer does");
            s.AppendLine("- **## Suggested Approach** — one concrete change");
            s.AppendLine("- **## Acceptance Criteria** — checkboxes; when you reproduced the problem, the reproduction passing is one of them");
        });

        PromptBuilder.AppendSection(sb, "## Output", s =>
        {
            s.AppendLine($"Write your result as JSON to `{AgentWorkspacePaths.TriageResultFilePath}`. Use exactly this shape:");
            s.AppendLine();
            s.AppendLine("```json");
            s.AppendLine("{");
            s.AppendLine("  \"verdict\": \"cause_found | inconclusive | not_a_bug | duplicate\",");
            s.AppendLine("  \"confidence\": \"high | medium | low\",");
            s.AppendLine("  \"summary\": \"The root cause (or the state of the investigation) in two or three sentences.\",");
            s.AppendLine("  \"impact\": \"Who or what is affected, and how much.\",");
            s.AppendLine("  \"reproduced\": true,");
            s.AppendLine("  \"reproduction\": \"How: for example the failing test and the command.\",");
            s.AppendLine("  \"duplicateOf\": \"For verdict duplicate: the issue link or number.\",");
            s.AppendLine("  \"causalChain\": [ { \"text\": \"One link from symptom to root cause\", \"kind\": \"symptom | step | trigger | root_cause\", \"evidenceIds\": [\"E1\"] } ],");
            s.AppendLine("  \"hypotheses\": [ { \"id\": \"H1\", \"text\": \"An explanation you considered\", \"state\": \"confirmed | ruled_out | open\" } ],");
            s.AppendLine("  \"evidence\": [ { \"id\": \"E1\", \"claim\": \"A fact\", \"source\": \"tool · target, e.g. grafana · Loki\", \"query\": \"The query, command or file:line\", \"link\": \"A URL, if there is one\" } ],");
            s.AppendLine("  \"investigated\": [ { \"check\": \"What you checked\", \"where\": \"tool · target\", \"for\": \"H1, symptom, duplicates, …\", \"result\": \"What it showed\", \"evidenceIds\": [\"E1\"] } ],");
            s.AppendLine("  \"notChecked\": [ { \"what\": \"Something you could not check\", \"why\": \"Why not, e.g. no trace source connected\" } ],");
            s.AppendLine("  \"questions\": [ { \"question\": \"For verdict inconclusive: what you need from a person\", \"why\": \"What it would decide\" } ],");
            s.AppendLine("  \"drafts\": [ { \"id\": \"d1\", \"kind\": \"root_fix | mitigation | prevention\", \"targetRepository\": \"repository name\", \"title\": \"Imperative issue title\", \"body\": \"Markdown with the sections above\", \"size\": \"S · 2 files\" } ]");
            s.AppendLine("}");
            s.AppendLine("```");
            s.AppendLine();
            s.AppendLine("- `investigated` is required and must not be empty.");
            s.AppendLine("- `questions` is required for `inconclusive`; `drafts` may be empty for any verdict other than `cause_found`.");
            s.AppendLine("- Do not write any other files except a reproduction test you keep local to the workspace.");
        });

        return sb.ToString();
    }

    /// <summary>Builds the prompt of the isolated reviewer that checks the result.</summary>
    public static string BuildReviewPrompt(bool reportToTracker)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Triage Review");
        sb.AppendLine();
        sb.AppendLine("You are an independent reviewer of a root cause analysis produced by another agent. You share no context");
        sb.AppendLine("with it: judge the result only by the files and by checking the evidence yourself with the same tools.");
        sb.AppendLine();

        PromptBuilder.AppendSection(sb, "## Input", s =>
        {
            s.AppendLine($"- The result: `{AgentWorkspacePaths.TriageResultFilePath}`");
            s.AppendLine($"- The report: `{AgentWorkspacePaths.IssueContextFilePath}` and `{AgentWorkspacePaths.TriageContextFilePath}`");
            s.AppendLine($"- Open issues: `{AgentWorkspacePaths.OpenIssuesDirectory}/`");
        });

        PromptBuilder.AppendSection(sb, "## Check", s =>
        {
            s.AppendLine("1. **Evidence** — every claim, chain link and investigated entry names a source and a query, command or file:line. Re-run a sample of them. A missing or wrong source is `[CRITICAL]`.");
            s.AppendLine("2. **Ruled-out hypotheses** — each was really tested and the evidence really contradicts it.");
            s.AppendLine("3. **Causation** — the chain does not present a coincidence in time as a cause, and has no missing link between symptom and root cause.");
            s.AppendLine("4. **Verdict and confidence** — they match the evidence. `cause_found` with weak evidence should be `inconclusive` with questions.");
            s.AppendLine("5. **Drafts** — each traces to the root cause, is one agent run, follows the body sections, and does not duplicate an open issue.");
            s.AppendLine("6. **Sensitive data** — no secrets, tokens, e-mail addresses, customer names or payment data are quoted.");
            if (reportToTracker)
                s.AppendLine("7. **Public tracker** — the result will be posted on a tracker issue: no pasted log lines or query output, and no quotes from other triages. Any is `[CRITICAL]`.");
        });

        PromptBuilder.AppendSection(sb, "## Output", s =>
        {
            s.AppendLine($"Write your findings to `{AgentWorkspacePaths.TriageReviewFilePath}`, one per line, with these markers:");
            s.AppendLine();
            s.AppendLine("- `[CRITICAL]` — wrong or unsupported; must be fixed");
            s.AppendLine("- `[WARNING]` — incomplete or weak; should be fixed");
            s.AppendLine("- `[SUGGESTION]` — optional");
            s.AppendLine();
            s.AppendLine("Only `[CRITICAL]` and `[WARNING]` trigger a refinement. If the result is sound, write \"No issues found\" and do not echo marker syntax.");
            s.AppendLine($"- {PromptBuilder.FindingLineRule}");
            s.AppendLine("- Do not modify the result file or any other file except the review file.");
        });

        return sb.ToString();
    }

    /// <summary>Builds the prompt sent back to the investigating agent when the review found problems.</summary>
    public static string BuildRefinementPrompt()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Triage Refinement");
        sb.AppendLine();
        sb.AppendLine("An independent reviewer checked your root cause analysis and found problems.");
        sb.AppendLine();

        PromptBuilder.AppendSection(sb, "## Instructions", s =>
        {
            s.AppendLine($"1. Read the findings in `{AgentWorkspacePaths.TriageReviewFilePath}`.");
            s.AppendLine("2. Fix every `[CRITICAL]` and `[WARNING]` finding. Check evidence again where the reviewer doubts it; add the checks you make to `investigated`.");
            s.AppendLine("3. If the evidence no longer supports your root cause, change the verdict to `inconclusive` and ask questions instead.");
            s.AppendLine($"4. Rewrite `{AgentWorkspacePaths.TriageResultFilePath}` in full, with the same JSON shape and rules as before.");
        });

        return sb.ToString();
    }
}
