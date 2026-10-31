using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Serilog;
using static CodingAgent.Pipeline.Brain.BrainReviewInputRenderer;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Agent.Executors;

/// <summary>
/// Builds and writes the brain review input file (.agent/brain-review-input.md).
/// Spec 054, Task 7A.3.
/// </summary>
public sealed class BrainReviewInputWriter
{
    private readonly IBrainUpdateService _brainUpdateService;
    private readonly ILogger _logger;

    public BrainReviewInputWriter(IBrainUpdateService brainUpdateService, ILogger logger)
    {
        _brainUpdateService = brainUpdateService;
        _logger = logger;
    }

    /// <summary>
    /// Builds and writes the review input file to the brain workspace.
    /// Non-fatal (T6).
    /// </summary>
    public async Task WriteAsync(
        string workspacePath,
        IReadOnlyList<BrainGroupMember>? group,
        CancellationToken ct)
    {
        try
        {
            var model = await BuildModelAsync(workspacePath, group, ct);
            var content = BrainReviewInputRenderer.Render(model);
            var outputPath = Path.Combine(workspacePath, AgentWorkspacePaths.BrainReviewInputFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, content,
                new System.Text.UTF8Encoding(false), ct);
            _logger.Information("Brain review input written: {InboxFiles} inbox files, {InboxFilesLeft} left",
                model.InboxFiles.Count, model.InboxFilesLeft);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to write brain review input: {Error}", ex.Message);
        }
    }

    private async Task<BrainReviewInputModel> BuildModelAsync(
        string workspacePath,
        IReadOnlyList<BrainGroupMember>? group,
        CancellationToken ct)
    {
        // ── Inbox files (D25: at most 100, newest first) ─────────────────────
        var trackedFiles = await _brainUpdateService.ListTrackedFilesAsync(workspacePath, ct);
        var inboxPaths = trackedFiles
            .Where(BrainLayout.IsInboxPath)
            .OrderByDescending(p => p) // yyyy-MM-dd_<runId>.md: newest first by date
            .ToList();

        var taken = inboxPaths.Take(BrainLayout.MaxInboxFilesPerReview).ToList();
        var inboxFilesLeft = inboxPaths.Count - taken.Count;

        var inboxFiles = new List<InboxFileEntry>();
        var malformed = new List<MalformedInboxEntry>();

        foreach (var path in taken)
        {
            try
            {
                var fullPath = Path.Combine(workspacePath, path.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(fullPath);
                if (info.Length > BrainLayout.MaxInboxBytes)
                {
                    malformed.Add(new MalformedInboxEntry(path, "over 16 KB"));
                    continue;
                }

                string text;
                try { text = await File.ReadAllTextAsync(fullPath, new System.Text.UTF8Encoding(false, true), ct); }
                catch { malformed.Add(new MalformedInboxEntry(path, "not UTF-8")); continue; }

                if (!BrainInboxHeaderParser.TryParse(text, out var header, out var error))
                {
                    malformed.Add(new MalformedInboxEntry(path, error ?? "broken header"));
                    continue;
                }

                inboxFiles.Add(new InboxFileEntry(path, header));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                malformed.Add(new MalformedInboxEntry(path, ex.Message));
            }
        }

        // ── Skills at injected commits (at most 10) ───────────────────────────
        var injectedCommits = inboxFiles
            .Select(f => f.Header?.SkillsCommit)
            .Where(sha => sha is not null)
            .Distinct()
            .Take(BrainLayout.MaxInjectedCommitsInReview)
            .ToList();

        var skillsAtCommits = new Dictionary<string, IReadOnlyList<SkillFileAtCommit>>(StringComparer.Ordinal);
        foreach (var sha in injectedCommits)
        {
            var files = new List<SkillFileAtCommit>();
            foreach (var skillPath in new[] { BrainLayout.GeneralSkillPath, BrainLayout.RepositorySkillPath("") }.Where(p => !string.IsNullOrEmpty(p)))
            {
                var content = _brainUpdateService.ReadFileAtCommit(workspacePath, sha!, skillPath);
                if (content is not null)
                {
                    var lines = content.Split('\n')
                        .Where(l => l.TrimStart().StartsWith("- [", StringComparison.Ordinal))
                        .ToList();
                    if (lines.Count > 0)
                        files.Add(new SkillFileAtCommit(skillPath, lines));
                }
            }
            skillsAtCommits[sha!] = files;
        }

        // ── Current skill files ───────────────────────────────────────────────
        var currentFiles = trackedFiles
            .Where(BrainLayout.IsSkillPath)
            .Select(p =>
            {
                var content = _brainUpdateService.ReadFileAtHead(workspacePath, p);
                var lineCount = content?.Split('\n')
                    .Count(l => l.TrimStart().StartsWith("- [", StringComparison.Ordinal)) ?? 0;
                return new CurrentSkillFile(p, lineCount);
            })
            .ToList();

        // ── Human commits ─────────────────────────────────────────────────────
        var humanCommits = _brainUpdateService.GetSkillCommitsSinceLastReview(workspacePath);

        // ── Group (derive from inbox headers if not provided) ─────────────────
        var effectiveGroup = group;
        if (effectiveGroup is null or { Count: 0 })
        {
            effectiveGroup = inboxFiles
                .Select(f => f.Header?.RepositoryFullName)
                .Where(r => r is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(fullName => new BrainGroupMember
                {
                    RepositoryFullName = fullName!,
                    RepositoryShortName = fullName!.Split('/').Last(),
                    ProjectName = inboxFiles.FirstOrDefault(f => f.Header?.RepositoryFullName == fullName)?.Header?.ProjectName,
                    StackLabels = inboxFiles.FirstOrDefault(f => f.Header?.RepositoryFullName == fullName)?.Header?.StackLabels ?? []
                })
                .ToList();
        }

        return new BrainReviewInputModel(
            Group: effectiveGroup,
            InboxFiles: inboxFiles,
            MalformedInbox: malformed,
            SkillsAtCommits: skillsAtCommits,
            CurrentFiles: currentFiles,
            HumanCommits: humanCommits,
            InboxFilesLeft: inboxFilesLeft);
    }
}
