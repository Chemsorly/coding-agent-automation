using System.Text.RegularExpressions;

namespace CodingAgent.Pipeline.Brain;

/// <summary>
/// Pattern helpers for skill lines and candidates (spec 054, req 6.3, 3.4).
/// All methods are pure — no I/O.
/// </summary>
public static partial class BrainLineFormat
{
    // ── Skill line ────────────────────────────────────────────────────
    //   - [<tag>] For <situation>, <what to do> (<refs>)[. missed again <refs>]
    [GeneratedRegex(
        @"^- \[(?<tag>2\+|user)\] (?<body>For .+?, .+?) \((?<refs>(?:#\d+|[A-Za-z0-9._-]+/[A-Za-z0-9._-]+#\d+)(?:, (?:#\d+|[A-Za-z0-9._-]+/[A-Za-z0-9._-]+#\d+))*)\)\.( missed again (?<missed>(?:#\d+|[A-Za-z0-9._-]+/[A-Za-z0-9._-]+#\d+)(?:, (?:#\d+|[A-Za-z0-9._-]+/[A-Za-z0-9._-]+#\d+))*))?$",
        RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex SkillLineRegex();

    // ── Candidate line ────────────────────────────────────────────────
    //   - [<scope>] For <situation>, <what to do> (<evidence>).
    [GeneratedRegex(
        @"^- \[(?<scope>repository|general|technology:[a-z0-9._-]+)\] For .+?, .+? \(.+\)\.$",
        RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex CandidateLineRegex();

    // ── URL / path / repo patterns for shared-content check ──────────
    [GeneratedRegex(@"https?://", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex UrlPattern();

    // A path: token containing / and ending in a known extension, OR token with two or more /
    // Trailing punctuation stripped before check (D23).
    [GeneratedRegex(
        @"(?<=\s|^)(?<tok>[^\s]+?)(?=[.,;:)\s]|$)",
        RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(
        @"\.[a-z]+$",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex FileExtPattern();

    // ── Public API ────────────────────────────────────────────────────

    /// <summary>
    /// Parses a skill line. Returns false when the line is over 300 characters or does not match.
    /// </summary>
    public static bool TryParseSkillLine(string line, out BrainSkillLine parsed)
    {
        parsed = default!;
        if (line.Length > BrainLayout.MaxLineLength) return false;
        Match m;
        try { m = SkillLineRegex().Match(line); }
        catch (RegexMatchTimeoutException) { return false; }
        if (!m.Success) return false;

        var refs = m.Groups["refs"].Value.Split(", ").ToList();
        var missedRaw = m.Groups["missed"].Value;
        var missed = string.IsNullOrEmpty(missedRaw)
            ? (IReadOnlyList<string>)[]
            : missedRaw.Split(", ").ToList();
        parsed = new BrainSkillLine(m.Groups["tag"].Value, m.Groups["body"].Value, refs, missed);
        return true;
    }

    /// <summary>
    /// Validates a candidate line. False when it has a line break, exceeds 300 chars, fails
    /// the pattern, or names a technology label not in <paramref name="stackLabels"/>.
    /// </summary>
    public static bool TryParseCandidate(string line, IReadOnlyCollection<string> stackLabels, out string scope)
    {
        scope = "";
        if (line.Contains('\r') || line.Contains('\n')) return false;
        if (line.Length > BrainLayout.MaxLineLength) return false;
        Match m;
        try { m = CandidateLineRegex().Match(line); }
        catch (RegexMatchTimeoutException) { return false; }
        if (!m.Success) return false;
        scope = m.Groups["scope"].Value;
        if (scope.StartsWith("technology:", StringComparison.Ordinal))
        {
            var label = scope["technology:".Length..];
            if (!stackLabels.Any(l => string.Equals(l.ToLowerInvariant(), label, StringComparison.Ordinal)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks a whole skill file for structural validity. Returns a result with IsValid = false
    /// and a reason when invalid.
    /// </summary>
    public static BrainSkillFileCheck CheckSkillFile(string content, int maxLines)
    {
        var lines = content.Split('\n');
        // Normalise CRLF
        lines = lines.Select(l => l.TrimEnd('\r')).ToArray();

        if (lines.Length == 0 || lines[0].TrimEnd() != BrainLayout.SkillMarker)
            return new BrainSkillFileCheck(false, "missing_marker", null, []);

        string? title = null;
        bool seenH2 = false;
        bool mostMissedFirst = true; // only the first ## is allowed to be Most missed
        bool seenNonMostMissedH2 = false;
        var skillLines = new List<BrainSkillLine>();

        for (int i = 1; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd();
            if (string.IsNullOrEmpty(l)) continue;

            if (l.StartsWith("## ", StringComparison.Ordinal))
            {
                seenH2 = true;
                if (l == "## Most missed")
                {
                    if (seenNonMostMissedH2)
                        mostMissedFirst = false;
                }
                else
                {
                    seenNonMostMissedH2 = true;
                }
                continue;
            }

            if (l.StartsWith("# ", StringComparison.Ordinal))
            {
                if (title is not null || seenH2)
                    return new BrainSkillFileCheck(false, "invalid_line", null, []);
                title = l[2..];
                continue;
            }

            if (l.StartsWith("- ", StringComparison.Ordinal))
            {
                if (!TryParseSkillLine(l, out var sl))
                    return new BrainSkillFileCheck(false, "invalid_line", null, []);
                skillLines.Add(sl);
                continue;
            }

            // Any other non-blank content is invalid
            return new BrainSkillFileCheck(false, "invalid_line", null, []);
        }

        if (!mostMissedFirst)
            return new BrainSkillFileCheck(false, "invalid_line", null, []);

        if (skillLines.Count > maxLines)
            return new BrainSkillFileCheck(false, "over_budget", title, skillLines);

        var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
        if (bytes > BrainLayout.MaxSkillFileBytes)
            return new BrainSkillFileCheck(false, "over_budget", title, skillLines);

        return new BrainSkillFileCheck(true, null, title, skillLines);
    }

    /// <summary>
    /// Returns max lines for a skill file path.
    /// </summary>
    public static int MaxLinesFor(string path) => path switch
    {
        _ when path.StartsWith("projects/", StringComparison.Ordinal) => BrainLayout.RepositorySkillMaxLines,
        _ when path.StartsWith("technology/", StringComparison.Ordinal) => BrainLayout.TechnologySkillMaxLines,
        _ => BrainLayout.GeneralSkillMaxLines
    };

    /// <summary>
    /// Checks body text (before refs) for shared content: URL, path or repository name.
    /// Returns the violation kind or null when clean. (Req 6.4, D23)
    /// </summary>
    public static string? FindSharedContentViolation(string body, IReadOnlyCollection<string> repositoryNames)
    {
        // URL check
        try
        {
            if (UrlPattern().IsMatch(body)) return "url";
        }
        catch (RegexMatchTimeoutException) { }

        // Path check: whitespace-delimited tokens, strip trailing .,;:)
        foreach (var token in body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.TrimEnd('.', ',', ';', ':', ')');
            if (IsPathToken(t)) return "path";
        }

        // Repository name check
        foreach (var name in repositoryNames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            // Use full name (owner/repo) or short name (last segment)
            var shortName = name.Contains('/') ? name.Split('/').Last() : name;
            if (shortName.Length < BrainLayout.MinCheckedRepositoryNameLength) continue;
            // Build regex for whole-word, case-insensitive match
            var pattern = $@"(?<![\\w-]){Regex.Escape(shortName)}(?![\\w-])";
            try
            {
                if (Regex.IsMatch(body, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                    return "repository";
            }
            catch (RegexMatchTimeoutException) { }
        }

        return null;
    }

    private static bool IsPathToken(string token)
    {
        if (!token.Contains('/')) return false;
        var slashes = token.Count(c => c == '/');
        if (slashes >= 2) return true;
        // One slash + ends in a file extension
        try
        {
            return FileExtPattern().IsMatch(token);
        }
        catch (RegexMatchTimeoutException) { return false; }
    }
}

/// <summary>Parsed skill line from a brain skill file.</summary>
public sealed record BrainSkillLine(
    string Tag,
    string Body,
    IReadOnlyList<string> Refs,
    IReadOnlyList<string> MissedAgainRefs);

/// <summary>Result of checking a skill file's structure.</summary>
public sealed record BrainSkillFileCheck(
    bool IsValid,
    string? Reason,
    string? Title,
    IReadOnlyList<BrainSkillLine> Lines);
