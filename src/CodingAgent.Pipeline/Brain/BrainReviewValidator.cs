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
        // 1. disallowed_path: any changed path that is not allowed
        foreach (var change in input.Changes)
        {
            if (BrainLayout.IsUnderAgentDirectory(change.Path)) continue;
            if (!IsAllowedChange(change, input.Changes)) 
                return new BrainReviewValidationResult(false, BrainReviewRefusalReasons.DisallowedPath, change.Path);
        }

        // 2. missing_marker, over_budget, invalid_line on changed skill paths
        foreach (var change in input.Changes)
        {
            if (!BrainLayout.IsSkillPath(change.Path)) continue;
            if (change.Kind == BrainPathChangeKind.Deleted) continue;
            if (!input.NewContents.TryGetValue(change.Path, out var content) || content is null) continue;
            var maxLines = BrainLineFormat.MaxLinesFor(change.Path);
            var check = BrainLineFormat.CheckSkillFile(content, maxLines);
            if (!check.IsValid)
                return new BrainReviewValidationResult(false, check.Reason!, change.Path);
        }

        // 3. shared_line_content: technology and general skill lines must not have repo names, URLs or paths
        foreach (var change in input.Changes)
        {
            if (!BrainLayout.IsSkillPath(change.Path)) continue;
            if (change.Path.StartsWith("projects/", StringComparison.Ordinal)) continue;
            if (change.Kind == BrainPathChangeKind.Deleted) continue;
            if (!input.NewContents.TryGetValue(change.Path, out var content) || content is null) continue;
            var maxLines = BrainLineFormat.MaxLinesFor(change.Path);
            var check = BrainLineFormat.CheckSkillFile(content, maxLines);
            if (!check.IsValid) continue; // already caught above
            foreach (var sl in check.Lines)
            {
                var violation = BrainLineFormat.FindSharedContentViolation(sl.Body, input.GroupRepositoryNames);
                if (violation is not null)
                    return new BrainReviewValidationResult(false, BrainReviewRefusalReasons.SharedLineContent, change.Path);
            }
        }

        // 4. shrank: a skill file that had at least ShrinkRuleMinLines lost more than half its lines
        foreach (var change in input.Changes)
        {
            if (!BrainLayout.IsSkillPath(change.Path)) continue;
            var headContent = input.HeadContents.GetValueOrDefault(change.Path);
            var newContent = change.Kind == BrainPathChangeKind.Deleted ? null
                : input.NewContents.GetValueOrDefault(change.Path);
            int before = CountSkillLines(headContent);
            if (before < BrainLayout.ShrinkRuleMinLines) continue;

            int removed = CountRemovedLines(headContent, newContent);
            if (removed * 2 > before)
                return new BrainReviewValidationResult(false, BrainReviewRefusalReasons.Shrank, change.Path);
        }

        // 5. pending_invalid: review/pending.md marker and line count
        if (input.Changes.Any(c => c.Path == BrainLayout.PendingPath && c.Kind != BrainPathChangeKind.Deleted))
        {
            if (input.NewContents.TryGetValue(BrainLayout.PendingPath, out var pendingContent) && pendingContent is not null)
            {
                var pendingLines = pendingContent.Split('\n');
                var firstLine = pendingLines.Length > 0 ? pendingLines[0].TrimEnd('\r') : "";
                if (firstLine != BrainLayout.PendingMarker)
                    return new BrainReviewValidationResult(false, BrainReviewRefusalReasons.PendingInvalid, BrainLayout.PendingPath);
                int nonBlank = pendingLines.Skip(1).Count(l => !string.IsNullOrWhiteSpace(l.TrimEnd('\r')));
                if (nonBlank > BrainLayout.PendingMaxLines)
                    return new BrainReviewValidationResult(false, BrainReviewRefusalReasons.PendingInvalid, BrainLayout.PendingPath);
            }
        }

        // 6. summary_missing / summary_invalid
        if (input.SummaryJson is null)
            return new BrainReviewValidationResult(false, BrainReviewRefusalReasons.SummaryMissing, null);
        var parseResult = BrainReviewSummaryParser.Parse(input.SummaryJson);
        if (parseResult.Error is not null)
            return new BrainReviewValidationResult(false, BrainReviewRefusalReasons.SummaryInvalid, null);

        return new BrainReviewValidationResult(true, null, null);
    }

    private static bool IsAllowedChange(BrainPathChange change, IReadOnlyList<BrainPathChange> allChanges)
    {
        if (BrainLayout.IsSkillPath(change.Path)) return true;
        if (change.Path == BrainLayout.PendingPath) return true;
        if (change.Path == BrainLayout.ReadmePath) return true;
        // Inbox deletions allowed
        if (change.Kind == BrainPathChangeKind.Deleted && BrainLayout.IsInboxPath(change.Path)) return true;
        // Archive move: deletion of non-v2 path + corresponding add under archive/v1/
        if (change.Kind == BrainPathChangeKind.Deleted && !BrainLayout.IsV2Path(change.Path))
        {
            var archivePath = $"{BrainLayout.ArchiveV1Directory}/{change.Path}";
            if (allChanges.Any(c => c.Path == archivePath && c.Kind == BrainPathChangeKind.Added))
                return true;
        }
        if (change.Kind == BrainPathChangeKind.Added && change.Path.StartsWith($"{BrainLayout.ArchiveV1Directory}/", StringComparison.Ordinal))
        {
            var originalPath = change.Path[($"{BrainLayout.ArchiveV1Directory}/").Length..];
            if (allChanges.Any(c => c.Path == originalPath && c.Kind == BrainPathChangeKind.Deleted))
                return true;
        }
        return false;
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
                foreach (var sl in newCheck.Lines)
                    newBodies.Add(sl.Body);
        }

        return headCheck.Lines.Count(sl => !newBodies.Contains(sl.Body));
    }
}
