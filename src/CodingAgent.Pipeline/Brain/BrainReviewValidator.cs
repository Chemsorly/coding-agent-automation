using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Brain;

/// <summary>Input for the brain review validator.</summary>
public sealed record BrainReviewValidationInput(
    IReadOnlyList<BrainPathChange> Changes,
    IReadOnlyDictionary<string, string?> HeadContents,
    IReadOnlyDictionary<string, string?> NewContents,
    IReadOnlyCollection<string> GroupRepositoryNames,
    string? SummaryJson);

/// <summary>Result of validating a brain review.</summary>
public sealed record BrainReviewValidationResult(bool IsValid, string? Reason, string? Detail);

/// <summary>
/// Validates the working-tree changes produced by a brain review agent before the review push
/// (spec 054, req 13.2, DD10). Pure — no I/O.
/// </summary>
public static class BrainReviewValidator
{
    public static BrainReviewValidationResult Validate(BrainReviewValidationInput input)
    {
        var result = CheckPaths(input.Changes);
        if (!result.IsValid) return result;

        result = CheckSkillFiles(input.Changes, input.NewContents, input.GroupRepositoryNames);
        if (!result.IsValid) return result;

        result = CheckShrink(input.Changes, input.HeadContents, input.NewContents);
        if (!result.IsValid) return result;

        result = CheckPending(input.Changes, input.NewContents);
        if (!result.IsValid) return result;

        return CheckSummary(input.SummaryJson);
    }

    // ── Path check ────────────────────────────────────────────────────

    private static BrainReviewValidationResult CheckPaths(IReadOnlyList<BrainPathChange> changes)
    {
        foreach (var change in changes)
        {
            if (!BrainLayout.IsUnderAgentDirectory(change.Path) && !IsAllowedChange(change, changes))
                return new BrainReviewValidationResult(false, BrainReviewRefusalReasons.DisallowedPath, change.Path);
        }
        return Valid;
    }

    private static bool IsAllowedChange(BrainPathChange change, IReadOnlyList<BrainPathChange> allChanges)
    {
        if (BrainLayout.IsSkillPath(change.Path)) return true;
        if (change.Path == BrainLayout.PendingPath) return true;
        if (change.Path == BrainLayout.ReadmePath) return true;
        if (change.Kind == BrainPathChangeKind.Deleted && BrainLayout.IsInboxPath(change.Path)) return true;
        return IsArchiveMovePair(change, allChanges);
    }

    private static bool IsArchiveMovePair(BrainPathChange change, IReadOnlyList<BrainPathChange> allChanges)
    {
        if (change.Kind == BrainPathChangeKind.Deleted && !BrainLayout.IsV2Path(change.Path))
        {
            var archivePath = $"{BrainLayout.ArchiveV1Directory}/{change.Path}";
            return allChanges.Any(c => c.Path == archivePath && c.Kind == BrainPathChangeKind.Added);
        }
        if (change.Kind == BrainPathChangeKind.Added && change.Path.StartsWith($"{BrainLayout.ArchiveV1Directory}/", StringComparison.Ordinal))
        {
            var originalPath = change.Path[($"{BrainLayout.ArchiveV1Directory}/").Length..];
            return allChanges.Any(c => c.Path == originalPath && c.Kind == BrainPathChangeKind.Deleted);
        }
        return false;
    }

    // ── Skill file checks ─────────────────────────────────────────────

    private static BrainReviewValidationResult CheckSkillFiles(
        IReadOnlyList<BrainPathChange> changes,
        IReadOnlyDictionary<string, string?> newContents,
        IReadOnlyCollection<string> groupRepositoryNames)
    {
        foreach (var change in changes)
        {
            if (!BrainLayout.IsSkillPath(change.Path) || change.Kind == BrainPathChangeKind.Deleted) continue;
            if (!newContents.TryGetValue(change.Path, out var content) || content is null) continue;

            var check = BrainLineFormat.CheckSkillFile(content, BrainLineFormat.MaxLinesFor(change.Path));
            if (!check.IsValid) return new BrainReviewValidationResult(false, check.Reason!, change.Path);

            if (!change.Path.StartsWith("projects/", StringComparison.Ordinal))
            {
                foreach (var sl in check.Lines)
                {
                    if (BrainLineFormat.FindSharedContentViolation(sl.Body, groupRepositoryNames) is not null)
                        return new BrainReviewValidationResult(false, BrainReviewRefusalReasons.SharedLineContent, change.Path);
                }
            }
        }
        return Valid;
    }

    // ── Shrink check ──────────────────────────────────────────────────

    private static BrainReviewValidationResult CheckShrink(
        IReadOnlyList<BrainPathChange> changes,
        IReadOnlyDictionary<string, string?> headContents,
        IReadOnlyDictionary<string, string?> newContents)
    {
        foreach (var change in changes)
        {
            if (!BrainLayout.IsSkillPath(change.Path)) continue;
            var headContent = headContents.GetValueOrDefault(change.Path);
            var before = CountSkillLines(headContent);
            if (before < BrainLayout.ShrinkRuleMinLines) continue;
            var newContent = change.Kind == BrainPathChangeKind.Deleted ? null : newContents.GetValueOrDefault(change.Path);
            if (CountRemovedLines(headContent, newContent) * 2 > before)
                return new BrainReviewValidationResult(false, BrainReviewRefusalReasons.Shrank, change.Path);
        }
        return Valid;
    }

    private static int CountSkillLines(string? content)
    {
        if (content is null) return 0;
        var check = BrainLineFormat.CheckSkillFile(content, int.MaxValue);
        return check.IsValid ? check.Lines.Count : 0;
    }

    private static int CountRemovedLines(string? headContent, string? newContent)
    {
        if (headContent is null) return 0;
        var headCheck = BrainLineFormat.CheckSkillFile(headContent, int.MaxValue);
        if (!headCheck.IsValid) return 0;
        var newBodies = new HashSet<string>(StringComparer.Ordinal);
        if (newContent is not null)
        {
            var newCheck = BrainLineFormat.CheckSkillFile(newContent, int.MaxValue);
            if (newCheck.IsValid)
                foreach (var sl in newCheck.Lines) newBodies.Add(sl.Body);
        }
        return headCheck.Lines.Count(sl => !newBodies.Contains(sl.Body));
    }

    // ── Pending check ─────────────────────────────────────────────────

    private static BrainReviewValidationResult CheckPending(
        IReadOnlyList<BrainPathChange> changes,
        IReadOnlyDictionary<string, string?> newContents)
    {
        if (!changes.Any(c => c.Path == BrainLayout.PendingPath && c.Kind != BrainPathChangeKind.Deleted))
            return Valid;
        if (!newContents.TryGetValue(BrainLayout.PendingPath, out var content) || content is null)
            return Valid;

        var lines = content.Split('\n');
        var firstLine = lines.Length > 0 ? lines[0].TrimEnd('\r') : "";
        if (firstLine != BrainLayout.PendingMarker)
            return new BrainReviewValidationResult(false, BrainReviewRefusalReasons.PendingInvalid, BrainLayout.PendingPath);

        int nonBlank = lines.Skip(1).Count(l => !string.IsNullOrWhiteSpace(l.TrimEnd('\r')));
        if (nonBlank > BrainLayout.PendingMaxLines)
            return new BrainReviewValidationResult(false, BrainReviewRefusalReasons.PendingInvalid, BrainLayout.PendingPath);

        return Valid;
    }

    // ── Summary check ─────────────────────────────────────────────────

    private static BrainReviewValidationResult CheckSummary(string? summaryJson)
    {
        if (summaryJson is null) return new BrainReviewValidationResult(false, BrainReviewRefusalReasons.SummaryMissing, null);
        var parseResult = BrainReviewSummaryParser.Parse(summaryJson);
        if (parseResult.Error is not null) return new BrainReviewValidationResult(false, BrainReviewRefusalReasons.SummaryInvalid, null);
        return Valid;
    }

    private static readonly BrainReviewValidationResult Valid = new(true, null, null);
}
