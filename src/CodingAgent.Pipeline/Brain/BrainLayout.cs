namespace CodingAgent.Pipeline.Brain;

/// <summary>
/// Constants and path helpers for the v2 brain repository layout (spec 054).
/// All methods are pure — no I/O, no clock.
/// </summary>
public static class BrainLayout
{
    // ── Directory / file names ────────────────────────────────────────
    public const string InboxDirectory = "inbox";
    public const string GeneralSkillPath = "general/SKILL.md";
    public const string PendingPath = "review/pending.md";
    public const string ArchiveV1Directory = "archive/v1";
    public const string ReadmePath = "README.md";

    // ── Markers ───────────────────────────────────────────────────────
    public const string InboxMarker = "<!-- brain:v2 inbox -->";
    public const string SkillMarker = "<!-- brain:v2 skill -->";
    public const string PendingMarker = "<!-- brain:v2 pending -->";

    // ── Budgets (req 16) ──────────────────────────────────────────────
    public const int MaxCandidates = 5;
    public const int MaxLineLength = 300;
    public const int MaxInboxBytes = 16_384;
    public const int MaxInjectedBytes = 16_384;
    public const int RepositorySkillMaxLines = 40;
    public const int TechnologySkillMaxLines = 25;
    public const int GeneralSkillMaxLines = 15;
    public const int MaxSkillFileBytes = 8_192;
    public const int PendingMaxLines = 200;
    public const int PendingExpiryDays = 60;
    public const int MaxChangedPaths = 20;
    public const int MaxFindingLines = 5;
    public const int MaxExcerptChars = 300;
    public const int MaxFailedTestNames = 20;
    public const int MaxReviewSummaryBytes = 65_536;
    public const int MaxEvidenceAttempts = 10;
    public const int MaxHumanComments = 10;
    public const int MaxCiLinesPerJob = 8;
    public const int MaxInjectedCommitsInReview = 10;
    public const int MaxInboxFilesPerReview = 100;
    public const int MinCheckedRepositoryNameLength = 4;
    public const int ShrinkRuleMinLines = 4;

    // ── Commit messages ───────────────────────────────────────────────
    public static string InboxCommitMessage(string runId, string issue) =>
        $"brain: inbox from run {runId} ({issue})";
    public static string ReviewCommitMessage(string runId) =>
        $"brain: review run {runId}";
    public const string ArchiveCommitMessage = "brain: archive v1 content";

    // ── Path builders ─────────────────────────────────────────────────

    /// <summary>Returns the inbox file path for a given date and run ID.</summary>
    public static string InboxFileName(DateOnly date, string runId) =>
        $"{InboxDirectory}/{date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}_{runId}.md";

    /// <summary>Returns the repository skill path for a given short name.</summary>
    public static string RepositorySkillPath(string shortName) =>
        $"projects/{shortName}/SKILL.md";

    /// <summary>
    /// Returns the technology skill path for a given label, or null when the lowercased label
    /// contains a character outside [a-z0-9._-] or is empty.
    /// </summary>
    public static string? TechnologySkillPath(string label)
    {
        if (string.IsNullOrEmpty(label))
            return null;
        var lower = label.ToLowerInvariant();
        if (lower.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '.' && ch != '_' && ch != '-'))
            return null;
        return $"technology/{lower}/SKILL.md";
    }

    // ── Path classification ───────────────────────────────────────────

    private static string Normalise(string path) => path.Replace('\\', '/');

    /// <summary>True for repository, technology, or general skill paths.</summary>
    public static bool IsSkillPath(string path)
    {
        path = Normalise(path);
        if (path == GeneralSkillPath) return true;
        var parts = path.Split('/');
        return parts.Length == 3 && parts[2] == "SKILL.md" &&
               (parts[0] == "projects" || parts[0] == "technology");
    }

    /// <summary>True for inbox files exactly one level below inbox/.</summary>
    public static bool IsInboxPath(string path)
    {
        path = Normalise(path);
        var parts = path.Split('/');
        return parts.Length == 2 && parts[0] == InboxDirectory && parts[1].EndsWith(".md", StringComparison.Ordinal);
    }

    /// <summary>True for paths under .agent/.</summary>
    public static bool IsUnderAgentDirectory(string path) =>
        Normalise(path).StartsWith(".agent/", StringComparison.Ordinal);

    /// <summary>
    /// True for paths that are part of the v2 layout: README.md, review/pending.md, skill paths,
    /// inbox paths, archive/, and .git* paths.
    /// </summary>
    public static bool IsV2Path(string path)
    {
        path = Normalise(path);
        if (path == ReadmePath) return true;
        if (path == PendingPath) return true;
        if (IsSkillPath(path)) return true;
        if (IsInboxPath(path)) return true;
        if (path.StartsWith("archive/", StringComparison.Ordinal)) return true;
        if (path.StartsWith(".git", StringComparison.Ordinal)) return true;
        return false;
    }
}
