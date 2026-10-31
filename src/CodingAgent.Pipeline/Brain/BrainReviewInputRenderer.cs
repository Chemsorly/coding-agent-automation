using System.Text;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Brain;

/// <summary>
/// Renders the brain review input model to a markdown document.
/// Pure — no I/O. Spec 054, Task 7A.3.
/// </summary>
public static class BrainReviewInputRenderer
{
    public sealed record BrainReviewInputModel(
        IReadOnlyList<BrainGroupMember>? Group,
        IReadOnlyList<InboxFileEntry> InboxFiles,
        IReadOnlyList<MalformedInboxEntry> MalformedInbox,
        IReadOnlyDictionary<string, IReadOnlyList<SkillFileAtCommit>> SkillsAtCommits,
        IReadOnlyList<CurrentSkillFile> CurrentFiles,
        IReadOnlyList<BrainCommitInfo> HumanCommits,
        int InboxFilesLeft);

    public sealed record InboxFileEntry(string Path, BrainInboxHeader? Header);
    public sealed record MalformedInboxEntry(string Path, string Reason);
    public sealed record SkillFileAtCommit(string Path, IReadOnlyList<string> Lines);
    public sealed record CurrentSkillFile(string Path, int LineCount);

    public static string Render(BrainReviewInputModel model)
    {
        var sb = new StringBuilder();
        RenderHeader(sb);
        RenderBrainGroup(sb, model);
        RenderInboxFiles(sb, model);
        RenderInboxOverflow(sb, model);
        RenderMalformedInbox(sb, model);
        RenderSkillsAtCommits(sb, model);
        RenderCurrentSkillFiles(sb, model);
        RenderHumanCommits(sb, model);
        return sb.ToString();
    }

    private static void RenderHeader(StringBuilder sb)
    {
        sb.AppendLine("# Brain review input");
        sb.AppendLine();
    }

    private static void RenderBrainGroup(StringBuilder sb, BrainReviewInputModel model)
    {
        sb.AppendLine("## Brain group");
        sb.AppendLine();
        if (model.Group is { Count: > 0 })
        {
            sb.AppendLine("| Repository | Short name | Project | Stack labels |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var m in model.Group)
                sb.AppendLine($"| {m.RepositoryFullName} | {m.RepositoryShortName} | {m.ProjectName ?? ""} | {string.Join(", ", m.StackLabels)} |");
        }
        else
        {
            sb.AppendLine("No group information available.");
        }
        sb.AppendLine();
    }

    private static void RenderInboxFiles(StringBuilder sb, BrainReviewInputModel model)
    {
        sb.AppendLine("## Inbox files");
        sb.AppendLine();
        foreach (var file in model.InboxFiles)
        {
            sb.AppendLine($"### {file.Path}");
            if (file.Header is { } h)
            {
                sb.AppendLine($"- run: {h.RunId}");
                sb.AppendLine($"- type: {h.RunType}");
                sb.AppendLine($"- outcome: {h.Outcome}");
                sb.AppendLine($"- repository: {h.RepositoryFullName}");
                if (h.ProjectName is not null)
                    sb.AppendLine($"- project: {h.ProjectName}");
                sb.AppendLine($"- issue: {h.Issue}");
                if (h.StackLabels.Count > 0)
                    sb.AppendLine($"- stack labels: {string.Join(", ", h.StackLabels)}");
            }
            sb.AppendLine();
        }
    }

    private static void RenderInboxOverflow(StringBuilder sb, BrainReviewInputModel model)
    {
        if (model.InboxFilesLeft > 0)
            sb.AppendLine($"Inbox files left for the next review: {model.InboxFilesLeft}");
        sb.AppendLine();
    }

    private static void RenderMalformedInbox(StringBuilder sb, BrainReviewInputModel model)
    {
        if (model.MalformedInbox.Count > 0)
        {
            sb.AppendLine("## Malformed inbox files (excluded)");
            sb.AppendLine();
            foreach (var bad in model.MalformedInbox)
                sb.AppendLine($"- {bad.Path}: {bad.Reason}");
            sb.AppendLine();
        }
    }

    private static void RenderSkillsAtCommits(StringBuilder sb, BrainReviewInputModel model)
    {
        sb.AppendLine("## Skills at injected commits");
        sb.AppendLine();
        foreach (var (sha, files) in model.SkillsAtCommits)
        {
            sb.AppendLine($"### Commit {sha[..Math.Min(12, sha.Length)]}");
            if (files.Count == 0)
            {
                sb.AppendLine("unknown commit");
            }
            else
            {
                foreach (var sf in files)
                {
                    sb.AppendLine($"#### {sf.Path}");
                    foreach (var line in sf.Lines)
                        sb.AppendLine(line);
                    sb.AppendLine();
                }
            }
            sb.AppendLine();
        }
    }

    private static void RenderCurrentSkillFiles(StringBuilder sb, BrainReviewInputModel model)
    {
        sb.AppendLine("## Current skill files");
        sb.AppendLine();
        foreach (var f in model.CurrentFiles)
            sb.AppendLine($"- {f.Path} ({f.LineCount} lines)");
        sb.AppendLine();
    }

    private static void RenderHumanCommits(StringBuilder sb, BrainReviewInputModel model)
    {
        sb.AppendLine("## Human commits since the last review");
        sb.AppendLine();
        if (model.HumanCommits.Count > 0)
            foreach (var c in model.HumanCommits)
                sb.AppendLine($"- {c.Sha[..Math.Min(8, c.Sha.Length)]}: {c.Message}");
        else
            sb.AppendLine("No human commits.");
    }
}
