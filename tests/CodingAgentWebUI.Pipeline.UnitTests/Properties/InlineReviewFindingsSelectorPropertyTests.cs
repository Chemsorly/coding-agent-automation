using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using static CodingAgentWebUI.Pipeline.UnitTests.Properties.InlineReviewGenerators;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for FindingsSelector: severity threshold filtering, MaxInlineComments cap,
/// severity ordering stability, consolidation at same file:line, and DiffSide always Right.
/// Feature: 026-inline-review-comments, Properties P4, P5, P6, P10, P13
/// </summary>
[Trait("Feature", "026-inline-review-comments")]
public class InlineReviewFindingsSelectorPropertyTests
{
    // ─── P4: Severity Threshold Filtering ───────────────────────────────────────

    /// <summary>
    /// P4: For any set of findings and any threshold, all findings in the output have
    /// severity >= threshold. No finding below the threshold appears in the output comments.
    /// **Validates: Requirements 8.2, 13.1**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property P4_SeverityThresholdFiltering()
    {
        var gen =
            from findings in GenFindings(1, 20)
            from settings in GenSettings()
            select (findings, settings);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (findings, settings) = tuple;

            var (comments, _) = FindingsSelector.Select(findings, settings);

            // All output comments should only contain findings at or above the threshold.
            // We verify this by checking that no comment body contains a severity prefix
            // that is below the threshold. Since FindingsSelector filters before formatting,
            // we verify indirectly: the total findings that pass the threshold filter
            // should be the only source of output comments.
            var eligibleFindings = findings
                .Where(f => f.FilePath is not null && (int)f.Severity >= (int)settings.SeverityThreshold)
                .ToList();

            // If no findings are eligible, output should be empty
            if (eligibleFindings.Count == 0)
            {
                comments.Should().BeEmpty();
            }
            else
            {
                // All comments must come from eligible findings only.
                // Verify by checking that comment count <= eligible count (after capping)
                var cap = Math.Clamp(settings.MaxInlineComments, 1, 50);
                var expectedMaxComments = Math.Min(eligibleFindings.Count, cap);

                // After consolidation, comment count may be less than expectedMaxComments
                // (multiple findings at same file:line become one comment)
                comments.Count.Should().BeLessThanOrEqualTo(expectedMaxComments);
            }

            // Additionally verify: no comment body contains a severity prefix for a level
            // below the threshold (direct content verification)
            foreach (var comment in comments)
            {
                if ((int)settings.SeverityThreshold > (int)FindingSeverity.Suggestion)
                {
                    // If threshold is Warning or Critical, Suggestion findings should not appear
                    if ((int)settings.SeverityThreshold > (int)FindingSeverity.Suggestion)
                    {
                        // Suggestion emoji should not appear unless threshold allows it
                        var hasSuggestionOnly = comment.Body.Contains("💡 **SUGGESTION**")
                            && !comment.Body.Contains("🟡 **WARNING**")
                            && !comment.Body.Contains("🔴 **CRITICAL**");

                        if ((int)settings.SeverityThreshold > (int)FindingSeverity.Suggestion)
                        {
                            // A comment that ONLY has suggestions should not exist
                            // (but consolidated comments may have suggestions alongside higher severities)
                            hasSuggestionOnly.Should().BeFalse(
                                "comments should not contain only suggestions when threshold is above Suggestion");
                        }
                    }
                }

                if ((int)settings.SeverityThreshold > (int)FindingSeverity.Warning)
                {
                    // If threshold is Critical, Warning findings should not appear alone
                    var hasWarningOnly = comment.Body.Contains("🟡 **WARNING**")
                        && !comment.Body.Contains("🔴 **CRITICAL**");

                    hasWarningOnly.Should().BeFalse(
                        "comments should not contain only warnings when threshold is Critical");
                }
            }
        });
    }

    // ─── P5: MaxInlineComments Cap Enforcement ──────────────────────────────────

    /// <summary>
    /// P5: For any settings, the number of output ReviewComments ≤ Math.Clamp(MaxInlineComments, 1, 50).
    /// The cap is enforced regardless of how many findings are provided.
    /// **Validates: Requirements 8.3, 13.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property P5_MaxInlineCommentsCap()
    {
        var gen =
            from findings in GenFindings(1, 60)
            from maxComments in Gen.Choose(-5, 100) // Include out-of-range values to test clamping
            from threshold in GenSeverity()
            from orderBySeverity in Gen.Elements(true, false)
            let settings = new InlineCommentSettings
            {
                Enabled = true,
                SeverityThreshold = threshold,
                MaxInlineComments = maxComments,
                OrderBySeverity = orderBySeverity,
                MaxRetries = 1
            }
            select (findings, settings);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (findings, settings) = tuple;

            var (comments, _) = FindingsSelector.Select(findings, settings);

            var effectiveCap = Math.Clamp(settings.MaxInlineComments, 1, 50);

            // The number of output comments must never exceed the effective cap.
            // Note: consolidation may reduce the count further (multiple findings at same
            // file:line become one comment), so we check <=.
            comments.Count.Should().BeLessThanOrEqualTo(effectiveCap,
                $"output comments ({comments.Count}) should not exceed effective cap ({effectiveCap})");
        });
    }

    /// <summary>
    /// P5 (boundary): When MaxInlineComments is below 1 or above 50, it is clamped to [1, 50].
    /// **Validates: Requirements 8.3**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property P5_MaxInlineCommentsCap_ClampedBoundary()
    {
        var gen =
            from findings in GenFindings(5, 30)
            from maxComments in Gen.Frequency(
                (1, Gen.Choose(-100, 0)),   // Below minimum → clamped to 1
                (1, Gen.Choose(51, 200)))   // Above maximum → clamped to 50
            let settings = new InlineCommentSettings
            {
                Enabled = true,
                SeverityThreshold = FindingSeverity.Suggestion, // Allow all findings
                MaxInlineComments = maxComments,
                OrderBySeverity = false,
                MaxRetries = 1
            }
            select (findings, settings, maxComments);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (findings, settings, rawMax) = tuple;

            var (comments, _) = FindingsSelector.Select(findings, settings);

            var effectiveCap = Math.Clamp(rawMax, 1, 50);
            comments.Count.Should().BeLessThanOrEqualTo(effectiveCap);
        });
    }

    // ─── P6: Severity Ordering Stability ────────────────────────────────────────

    /// <summary>
    /// P6: When OrderBySeverity is true, the output comments are ordered by severity descending
    /// (Critical first). When false, the original order is preserved.
    /// **Validates: Requirements 8.4, 13.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property P6_SeverityOrderingStability_OrderEnabled()
    {
        // Generate findings that target distinct file:line combinations to avoid consolidation
        var gen =
            from count in Gen.Choose(3, 15)
            from findings in GenDistinctLocationFindings(count)
            let settings = new InlineCommentSettings
            {
                Enabled = true,
                SeverityThreshold = FindingSeverity.Suggestion, // Allow all
                MaxInlineComments = 50, // High cap to avoid truncation
                OrderBySeverity = true,
                MaxRetries = 1
            }
            select (findings, settings);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (findings, settings) = tuple;

            var (comments, _) = FindingsSelector.Select(findings, settings);

            // When OrderBySeverity is true, comments should be in severity-descending order.
            // Since each finding has a unique location (no consolidation), we can verify
            // the order by checking the severity prefixes in the comment bodies.
            var severities = comments.Select(c => ExtractSeverityFromBody(c.Body)).ToList();

            for (var i = 0; i < severities.Count - 1; i++)
            {
                severities[i].Should().BeGreaterThanOrEqualTo(severities[i + 1],
                    $"comment at index {i} (severity {severities[i]}) should be >= comment at index {i + 1} (severity {severities[i + 1]})");
            }
        });
    }

    /// <summary>
    /// P6 (order disabled): When OrderBySeverity is false, the original input order is preserved.
    /// Findings appear in the same relative order as they were provided.
    /// **Validates: Requirements 8.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property P6_SeverityOrderingStability_OrderDisabled()
    {
        // Generate findings with distinct locations to avoid consolidation effects
        var gen =
            from count in Gen.Choose(3, 15)
            from findings in GenDistinctLocationFindings(count)
            let settings = new InlineCommentSettings
            {
                Enabled = true,
                SeverityThreshold = FindingSeverity.Suggestion, // Allow all
                MaxInlineComments = 50, // High cap to avoid truncation
                OrderBySeverity = false,
                MaxRetries = 1
            }
            select (findings, settings);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (findings, settings) = tuple;

            var (comments, _) = FindingsSelector.Select(findings, settings);

            // When OrderBySeverity is false, the output should preserve the original order.
            // Since each finding has a unique location, each comment maps to exactly one finding.
            // Verify that the file paths appear in the same order as the input.
            var inputPaths = findings.Select(f => f.FilePath!).ToList();
            var outputPaths = comments.Select(c => c.Path).ToList();

            // Each output path should appear in the same relative order as in input
            var inputIndices = outputPaths.Select(p => inputPaths.IndexOf(p)).ToList();
            for (var i = 0; i < inputIndices.Count - 1; i++)
            {
                inputIndices[i].Should().BeLessThan(inputIndices[i + 1],
                    "output order should preserve input order when OrderBySeverity is false");
            }
        });
    }

    // ─── P10: Consolidation at Same File:Line ───────────────────────────────────

    /// <summary>
    /// P10: When multiple findings target the same (FilePath, LineNumber), they are consolidated
    /// into a single ReviewComment. The output should have at most one comment per unique (path, line).
    /// **Validates: Requirements 5.4, 13.3**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property P10_ConsolidationAtSameFileLine()
    {
        // Generate findings where some share the same file:line
        var gen =
            from sharedPath in GenFilePath()
            from sharedLine in GenLineNumber()
            from duplicateCount in Gen.Choose(2, 5)
            from duplicates in Gen.ArrayOf<StructuredFinding>(GenFindingAtLocation(sharedPath, sharedLine), duplicateCount)
            from uniqueCount in Gen.Choose(1, 5)
            from uniques in Gen.ArrayOf<StructuredFinding>(GenFinding(), uniqueCount)
            let allFindings = duplicates.Concat(uniques).ToList()
            let settings = new InlineCommentSettings
            {
                Enabled = true,
                SeverityThreshold = FindingSeverity.Suggestion, // Allow all
                MaxInlineComments = 50, // High cap
                OrderBySeverity = false,
                MaxRetries = 1
            }
            select (allFindings, settings, sharedPath, sharedLine);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (allFindings, settings, sharedPath, sharedLine) = tuple;

            var (comments, _) = FindingsSelector.Select(allFindings, settings);

            // Verify: no two comments share the same (Path, Line) combination
            var pathLinePairs = comments.Select(c => (c.Path, c.Line)).ToList();
            var distinctPairs = pathLinePairs.Distinct().ToList();

            pathLinePairs.Count.Should().Be(distinctPairs.Count,
                "each (Path, Line) combination should appear at most once in the output");

            // Verify: the shared location appears at most once
            var sharedComments = comments.Where(c => c.Path == sharedPath && c.Line == sharedLine).ToList();
            sharedComments.Count.Should().BeLessThanOrEqualTo(1,
                $"findings at {sharedPath}:{sharedLine} should be consolidated into a single comment");
        });
    }

    /// <summary>
    /// P10 (general uniqueness): For any input, the output never contains two comments
    /// with the same (Path, Line) pair.
    /// **Validates: Requirements 5.4, 13.3**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property P10_ConsolidationGeneral_NoDuplicatePathLine()
    {
        var gen =
            from findings in GenFindings(1, 30)
            from settings in GenSettings()
            select (findings, settings);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (findings, settings) = tuple;

            var (comments, _) = FindingsSelector.Select(findings, settings);

            // No two comments should have the same (Path, Line) combination
            var pathLinePairs = comments.Select(c => (c.Path, c.Line)).ToList();
            var distinctPairs = pathLinePairs.Distinct().ToList();

            pathLinePairs.Count.Should().Be(distinctPairs.Count,
                "output should never contain duplicate (Path, Line) pairs");
        });
    }

    // ─── P13: DiffSide Always Right Invariant ───────────────────────────────────

    /// <summary>
    /// P13: All output ReviewComments have Side == DiffSide.Right. The FindingsSelector
    /// always sets Side to Right because findings target the new file state.
    /// **Validates: Requirements 3.4 (design note: DiffSide always Right)**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property P13_DiffSideAlwaysRight()
    {
        var gen =
            from findings in GenFindings(1, 30)
            from settings in GenSettings()
            select (findings, settings);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (findings, settings) = tuple;

            var (comments, _) = FindingsSelector.Select(findings, settings);

            foreach (var comment in comments)
            {
                comment.Side.Should().Be(DiffSide.Right,
                    "all output ReviewComments must have Side == DiffSide.Right");
            }
        });
    }

    // ─── Helper Methods ─────────────────────────────────────────────────────────

    /// <summary>
    /// Generates findings with distinct (FilePath, LineNumber) combinations to avoid consolidation.
    /// </summary>
    private static Gen<IReadOnlyList<StructuredFinding>> GenDistinctLocationFindings(int count)
    {
        return Gen.ArrayOf<StructuredFinding>(GenFinding(), count).Select(findings =>
        {
            // Ensure unique locations by appending index to file path
            var result = new List<StructuredFinding>(count);
            for (var i = 0; i < findings.Length; i++)
            {
                result.Add(findings[i] with { FilePath = $"{findings[i].FilePath}_{i}", LineNumber = i + 1 });
            }
            return (IReadOnlyList<StructuredFinding>)result;
        });
    }

    /// <summary>
    /// Generates a finding at a specific file path and line number.
    /// </summary>
    private static Gen<StructuredFinding> GenFindingAtLocation(string filePath, int lineNumber) =>
        from severity in GenSeverity()
        from message in GenMessage()
        from agent in Gen.Elements(AgentNames)
        select new StructuredFinding
        {
            Severity = severity,
            FilePath = filePath,
            LineNumber = lineNumber,
            Message = message,
            AgentName = agent
        };

    /// <summary>
    /// Extracts the severity level from a formatted comment body by checking for emoji prefixes.
    /// Returns the numeric severity value (Critical=2, Warning=1, Suggestion=0).
    /// </summary>
    private static int ExtractSeverityFromBody(string body)
    {
        if (body.Contains("🔴 **CRITICAL**"))
            return (int)FindingSeverity.Critical;
        if (body.Contains("🟡 **WARNING**"))
            return (int)FindingSeverity.Warning;
        return (int)FindingSeverity.Suggestion;
    }
}
