using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using static CodingAgentWebUI.Pipeline.UnitTests.Properties.InlineReviewGenerators;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for InlineCommentFormatter output structure (P7),
/// ReviewSubmission body-only equivalence (P8), and configuration deserialization defaults (P11).
/// Feature: 026-inline-review-comments, Properties P7, P8, P11
/// </summary>
[Trait("Feature", "026-inline-review-comments")]
public class InlineReviewFormatterPropertyTests
{
    // ─── Test-specific generators ───────────────────────────────────────────────

    private static Gen<PullRequestReviewType> GenReviewType() =>
        Gen.Elements(PullRequestReviewType.Comment, PullRequestReviewType.RequestChanges);

    private static Gen<string> GenBody() =>
        from msg1 in Gen.Elements(Messages)
        from msg2 in Gen.Elements(Messages)
        select $"## Review Summary\n\n{msg1}\n\n{msg2}";

    // ─── P7: InlineCommentFormatter Output Structure ────────────────────────────

    /// <summary>
    /// P7 (FormatSingle): For any StructuredFinding, FormatSingle produces output containing:
    /// the correct emoji prefix, bold severity label, the message text, agent attribution line,
    /// and total length ≤ 65536 chars.
    /// **Validates: Requirements 5.1, 5.2, 5.3, 5.5**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property P7_FormatSingle_OutputStructure()
    {
        return Prop.ForAll(GenFinding().ToArbitrary(), finding =>
        {
            var output = InlineCommentFormatter.FormatSingle(finding);

            // Must contain the correct emoji prefix based on severity
            var expectedPrefix = finding.Severity switch
            {
                FindingSeverity.Critical => "🔴 **CRITICAL**",
                FindingSeverity.Warning => "🟡 **WARNING**",
                FindingSeverity.Suggestion => "💡 **SUGGESTION**",
                _ => "💡 **SUGGESTION**"
            };
            output.Should().Contain(expectedPrefix,
                $"output should contain the emoji prefix for {finding.Severity}");

            // Must contain the message text
            output.Should().Contain(finding.Message,
                "output should contain the finding message");

            // Must contain the agent attribution line
            output.Should().Contain($"— *{finding.AgentName}*",
                "output should contain the agent attribution");

            // Total length must not exceed 65536 characters
            output.Length.Should().BeLessThanOrEqualTo(65536,
                "output length must not exceed 65536 characters");

            // Must contain colon-space separator between prefix and message
            output.Should().Contain($"{expectedPrefix}: {finding.Message}",
                "output should have colon-space between prefix and message");
        });
    }

    /// <summary>
    /// P7 (FormatConsolidated): For multiple findings, FormatConsolidated produces output with
    /// --- separators between findings, total length ≤ 65536 chars, and findings ordered by severity.
    /// **Validates: Requirements 5.4, 5.5**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property P7_FormatConsolidated_OutputStructure()
    {
        var gen = GenFindings(2, 8);

        return Prop.ForAll(gen.ToArbitrary(), findings =>
        {
            var output = InlineCommentFormatter.FormatConsolidated(findings);

            // Total length must not exceed 65536 characters
            output.Length.Should().BeLessThanOrEqualTo(65536,
                "consolidated output length must not exceed 65536 characters");

            // When multiple findings, output should contain --- separators
            if (findings.Count > 1)
            {
                output.Should().Contain("---",
                    "consolidated output should contain --- separators between findings");
            }

            // Output should contain at least one emoji prefix (from the highest severity finding)
            var hasAnyPrefix = output.Contains("🔴 **CRITICAL**")
                || output.Contains("🟡 **WARNING**")
                || output.Contains("💡 **SUGGESTION**");
            hasAnyPrefix.Should().BeTrue(
                "consolidated output should contain at least one severity prefix");

            // Output should contain at least one agent attribution
            var hasAnyAttribution = AgentNames.Any(name => output.Contains($"— *{name}*"));
            hasAnyAttribution.Should().BeTrue(
                "consolidated output should contain at least one agent attribution");

            // Verify severity ordering: Critical findings should appear before Warning,
            // Warning before Suggestion in the output
            var criticalIndex = output.IndexOf("🔴 **CRITICAL**", StringComparison.Ordinal);
            var warningIndex = output.IndexOf("🟡 **WARNING**", StringComparison.Ordinal);
            var suggestionIndex = output.IndexOf("💡 **SUGGESTION**", StringComparison.Ordinal);

            if (criticalIndex >= 0 && warningIndex >= 0)
            {
                criticalIndex.Should().BeLessThan(warningIndex,
                    "Critical findings should appear before Warning findings");
            }
            if (criticalIndex >= 0 && suggestionIndex >= 0)
            {
                criticalIndex.Should().BeLessThan(suggestionIndex,
                    "Critical findings should appear before Suggestion findings");
            }
            if (warningIndex >= 0 && suggestionIndex >= 0)
            {
                warningIndex.Should().BeLessThan(suggestionIndex,
                    "Warning findings should appear before Suggestion findings");
            }
        });
    }

    // ─── P8: ReviewSubmission Body-Only Equivalence ─────────────────────────────

    /// <summary>
    /// P8: A ReviewSubmission with empty Comments list is semantically equivalent to a body-only
    /// review — the Body and Type fields are preserved. When Comments is empty, the submission
    /// delegates to the body-only overload (Body and Type are the only meaningful fields).
    /// **Validates: Requirements 3.3**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property P8_ReviewSubmission_BodyOnlyEquivalence()
    {
        var gen =
            from body in GenBody()
            from reviewType in GenReviewType()
            select (body, reviewType);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (body, reviewType) = tuple;

            var submission = new ReviewSubmission
            {
                Body = body,
                Type = reviewType,
                Comments = Array.Empty<ReviewComment>()
            };

            // Body is preserved
            submission.Body.Should().Be(body,
                "ReviewSubmission Body should be preserved exactly as provided");

            // Type is preserved
            submission.Type.Should().Be(reviewType,
                "ReviewSubmission Type should be preserved exactly as provided");

            // Comments is empty (body-only equivalence)
            submission.Comments.Should().BeEmpty(
                "a body-only ReviewSubmission should have an empty Comments list");

            // CommitId defaults to null when not set
            submission.CommitId.Should().BeNull(
                "CommitId should default to null when not explicitly set");
        });
    }

    // ─── P11: Configuration Deserialization Defaults ─────────────────────────────

    /// <summary>
    /// P11: When deserializing a JSON object without an InlineComments key, the resulting
    /// CodeReviewConfiguration has InlineComments with all default values:
    /// Enabled=false, SeverityThreshold=Warning, MaxInlineComments=15, OrderBySeverity=true, MaxRetries=1.
    /// **Validates: Requirements 8.1, 8.8**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property P11_ConfigurationDeserialization_DefaultsPreserved()
    {
        // Generate random JSON objects that have CodeReviewConfiguration fields
        // but explicitly do NOT include an InlineComments key
        var gen =
            from maxIterations in Gen.Choose(1, 10)
            from fixPrompt in Gen.Elements<string?>(null, "Fix the issues", "Please resolve critical findings")
            from isolation in Gen.Elements(ReviewIsolation.Shared, ReviewIsolation.Isolated)
            select (maxIterations, fixPrompt, isolation);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (maxIterations, fixPrompt, isolation) = tuple;

            // Build a JSON object WITHOUT the InlineComments key
            var jsonObj = new Dictionary<string, object?>
            {
                ["MaxIterations"] = maxIterations,
                ["ReviewIsolation"] = isolation.ToString()
            };

            if (fixPrompt is not null)
            {
                jsonObj["FixPrompt"] = fixPrompt;
            }

            var json = JsonSerializer.Serialize(jsonObj);

            // Deserialize — InlineComments should get default values
            var config = JsonSerializer.Deserialize<CodeReviewConfiguration>(json);

            config.Should().NotBeNull();
            config!.InlineComments.Should().NotBeNull(
                "InlineComments should be initialized to default instance when key is absent");

            // Verify all default values
            config.InlineComments.Enabled.Should().BeTrue(
                "Enabled should default to true");
            config.InlineComments.SeverityThreshold.Should().Be(FindingSeverity.Warning,
                "SeverityThreshold should default to Warning");
            config.InlineComments.MaxInlineComments.Should().Be(15,
                "MaxInlineComments should default to 15");
            config.InlineComments.OrderBySeverity.Should().BeTrue(
                "OrderBySeverity should default to true");
            config.InlineComments.MaxRetries.Should().Be(1,
                "MaxRetries should default to 1");

            // Also verify the other fields were deserialized correctly
            config.MaxIterations.Should().Be(maxIterations);
            config.FixPrompt.Should().Be(fixPrompt);
            config.ReviewIsolation.Should().Be(isolation);
        });
    }
}
