namespace CodingAgent.Pipeline.Brain;

/// <summary>A tracked file in the brain repository for archiver planning.</summary>
public sealed record BrainTrackedFile(string Path, string? FirstLine);

/// <summary>An archiver move: from → archive/v1/from.</summary>
public sealed record BrainArchiveMove(string From, string To);

/// <summary>The README.md template written by the first v2 review (spec 054, req 1.3).</summary>
public static class BrainReadmeTemplate
{
    public const string Text = """
        <!-- brain:v2 readme -->
        # Brain

        This repository is written by the coding agent pipeline (spec 054).

        - `inbox/` — one file per run: the agent's candidate lessons and the evidence the pipeline recorded.
        - `projects/<repository>/SKILL.md`, `technology/<stack label>/SKILL.md`, `general/SKILL.md` — curated skills that
          every run gets as steering.
        - `review/pending.md` — causes seen once, waiting for a second occurrence.
        - `archive/v1/` — the content of the first brain version. It is never used.

        Runs write only their inbox file. Only the brain review changes skills, `review/pending.md` and this file. People may
        edit skill files; the next review keeps their lines and tags them `user`.

        A skill file starts with `<!-- brain:v2 skill -->` and holds lines of this form:
        `- [2+] For <situation>, <what to do> (#123, owner/repo#45).` Budgets: 40 lines per repository skill, 25 per
        technology skill, 15 for the general skill, 8 KB per file.

        Every repository that uses this brain can read all of it. Repositories that must not share this must use separate brains.
        """;
}

/// <summary>
/// Plans which files to move to archive/v1/ for v1→v2 migration (spec 054, req 7.10). Pure.
/// </summary>
public static class BrainV1Archiver
{
    public static IReadOnlyList<BrainArchiveMove> Plan(IReadOnlyList<BrainTrackedFile> files)
    {
        var moves = new List<BrainArchiveMove>();
        foreach (var file in files)
        {
            var path = file.Path.Replace('\\', '/');
            if (ShouldSkip(path)) continue;
            if (ShouldArchive(path, file.FirstLine))
                moves.Add(new BrainArchiveMove(path, $"{BrainLayout.ArchiveV1Directory}/{path}"));
        }
        return moves;
    }

    private static bool ShouldSkip(string path) =>
        path.StartsWith("archive/", StringComparison.Ordinal) ||
        path.StartsWith(".git", StringComparison.Ordinal);

    private static bool ShouldArchive(string path, string? firstLine)
    {
        if (!BrainLayout.IsV2Path(path)) return true;
        if (BrainLayout.IsSkillPath(path)) return IsUnmarkedV1File(firstLine, BrainLayout.SkillMarker);
        if (path == BrainLayout.PendingPath) return IsUnmarkedV1File(firstLine, BrainLayout.PendingMarker);
        return false;
    }

    private static bool IsUnmarkedV1File(string? firstLine, string expectedMarker) =>
        firstLine?.TrimEnd('\r') != expectedMarker;
}
