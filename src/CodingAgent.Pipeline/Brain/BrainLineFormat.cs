using System.Text.RegularExpressions;

namespace CodingAgent.Pipeline.Brain;

/// <summary>
/// Pattern helpers for skill lines and candidates (spec 054, req 6.3, 3.4).
/// All methods are pure — no I/O.
/// </summary>
public static partial class BrainLineFormat
{
    private const string ReasonInvalidLine = "invalid_line";
    private const string ReasonMissingMarker = "missing_marker";
    private const string ReasonOverBudget = "over_budget";

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

    [GeneratedRegex(@"\.[a-z]+$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
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
    /// Checks a whole skill file for structural validity.
    /// </summary>
    public static BrainSkillFileCheck CheckSkillFile(string content, int maxLines)
    {
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        if (lines.Length == 0 || lines[0].TrimEnd() != BrainLayout.SkillMarker)
            return new BrainSkillFileCheck(false, ReasonMissingMarker, null, []);

        var result = ParseSkillFileBody(lines);
        if (!result.IsValid) return result;

        var skillLines = result.Lines;
        var title = result.Title;

        if (skillLines.Count > maxLines)
            return new BrainSkillFileCheck(false, ReasonOverBudget, title, skillLines);

        if (System.Text.Encoding.UTF8.GetByteCount(content) > BrainLayout.MaxSkillFileBytes)
            return new BrainSkillFileCheck(false, ReasonOverBudget, title, skillLines);

        return new BrainSkillFileCheck(true, null, title, skillLines);
    }

    private static BrainSkillFileCheck ParseSkillFileBody(string[] lines)
    {
        string? title = null;
        bool seenH2 = false;
        bool mostMissedFirst = true;
        bool seenNonMostMissedH2 = false;
        var skillLines = new List<BrainSkillLine>();

        for (int i = 1; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd();
            if (string.IsNullOrEmpty(l)) continue;

            if (l.StartsWith("## ", StringComparison.Ordinal))
            {
                seenH2 = true;
                if (l == "## Most missed" && seenNonMostMissedH2)
                    mostMissedFirst = false;
                else if (l != "## Most missed")
                    seenNonMostMissedH2 = true;
                continue;
            }

            if (l.StartsWith("# ", StringComparison.Ordinal))
            {
                if (title is not null || seenH2)
                    return new BrainSkillFileCheck(false, ReasonInvalidLine, null, []);
                title = l[2..];
                continue;
            }

            if (l.StartsWith("- ", StringComparison.Ordinal))
            {
                if (!TryParseSkillLine(l, out var sl))
                    return new BrainSkillFileCheck(false, ReasonInvalidLine, null, []);
                skillLines.Add(sl);
                continue;
            }

            return new BrainSkillFileCheck(false, ReasonInvalidLine, null, []);
        }

        if (!mostMissedFirst)
            return new BrainSkillFileCheck(false, ReasonInvalidLine, null, []);

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
    /// Checks body text for shared content violations: URL, path, or repository name.
    /// Returns the violation kind or null when clean. (Req 6.4, D23)
    /// </summary>
    public static string? FindSharedContentViolation(string body, IReadOnlyCollection<string> repositoryNames)
    {
        if (ContainsUrl(body)) return "url";
        if (ContainsPath(body)) return "path";
        if (ContainsRepositoryName(body, repositoryNames)) return "repository";
        return null;
    }

    private static bool ContainsUrl(string body)
    {
        try { return UrlPattern().IsMatch(body); }
        catch (RegexMatchTimeoutException) { return false; } // Timeout → no match
    }

    private static bool ContainsPath(string body)
    {
        foreach (var token in body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsPathToken(token.TrimEnd('.', ',', ';', ':', ')')))
                return true;
        }
        return false;
    }

    private static bool ContainsRepositoryName(string body, IReadOnlyCollection<string> repositoryNames)
    {
        foreach (var name in repositoryNames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            var shortName = name.Contains('/') ? name.Split('/')[^1] : name;
            if (shortName.Length < BrainLayout.MinCheckedRepositoryNameLength) continue;
            if (IsWholeWordMatch(body, shortName)) return true;
        }
        return false;
    }

    private static bool IsWholeWordMatch(string body, string term)
    {
        var pattern = $@"(?<![\\w-]){Regex.Escape(term)}(?![\\w-])";
        try
        {
            return Regex.IsMatch(body, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        }
        catch (RegexMatchTimeoutException) { return false; } // Timeout → no match
    }

    private static bool IsPathToken(string token)
    {
        if (!token.Contains('/')) return false;
        if (token.Count(c => c == '/') >= 2) return true;
        try { return FileExtPattern().IsMatch(token); }
        catch (RegexMatchTimeoutException) { return false; } // Timeout → no match
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
