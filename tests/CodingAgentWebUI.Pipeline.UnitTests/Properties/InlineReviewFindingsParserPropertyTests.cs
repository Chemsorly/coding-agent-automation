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
/// Property-based tests for FindingsParser: severity extraction, file:line extraction,
/// one-finding-per-line invariant, cross-parser invariant, and message extraction.
/// Feature: 026-inline-review-comments, Properties P1, P2, P3, P9, P12
/// </summary>
[Trait("Feature", "026-inline-review-comments")]
public class InlineReviewFindingsParserPropertyTests
{
    // ─── P1: Severity Extraction Accuracy ───────────────────────────────────────

    /// <summary>
    /// P1: For any input line containing exactly one severity marker [CRITICAL], [WARNING],
    /// or [SUGGESTION], FindingsParser.Parse produces a finding with the corresponding
    /// FindingSeverity value.
    /// **Validates: Requirements 1.3, 2.1**
    /// </summary>
    [Property(MaxTest = 20)]
    public Property P1_SeverityExtractionAccuracy()
    {
        var gen =
            from severity in Gen.Elements(Severities)
            from message in GenMessage()
            select (severity, $"[{severity}] {message}");

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (severityName, input) = tuple;
            var expectedSeverity = severityName switch
            {
                "CRITICAL" => FindingSeverity.Critical,
                "WARNING" => FindingSeverity.Warning,
                "SUGGESTION" => FindingSeverity.Suggestion,
                _ => FindingSeverity.Suggestion
            };

            var findings = FindingsParser.Parse(input, "TestAgent");

            findings.Should().HaveCount(1);
            findings[0].Severity.Should().Be(expectedSeverity);
        });
    }

    /// <summary>
    /// P1 (case-insensitive variant): Severity markers in any case produce the correct severity.
    /// **Validates: Requirements 2.6**
    /// </summary>
    [Property(MaxTest = 20)]
    public Property P1_SeverityExtractionCaseInsensitive()
    {
        var gen =
            from severity in Gen.Elements(Severities)
            from caseVariant in Gen.Choose(0, 2)
            from message in GenMessage()
            let formatted = caseVariant switch
            {
                0 => severity.ToUpperInvariant(),
                1 => severity.ToLowerInvariant(),
                _ => char.ToUpper(severity[0]) + severity[1..].ToLowerInvariant()
            }
            select (severity, $"[{formatted}] {message}");

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (severityName, input) = tuple;
            var expectedSeverity = severityName switch
            {
                "CRITICAL" => FindingSeverity.Critical,
                "WARNING" => FindingSeverity.Warning,
                "SUGGESTION" => FindingSeverity.Suggestion,
                _ => FindingSeverity.Suggestion
            };

            var findings = FindingsParser.Parse(input, "TestAgent");

            findings.Should().HaveCount(1);
            findings[0].Severity.Should().Be(expectedSeverity);
        });
    }

    // ─── P2: File:Line Extraction in Four Formats ───────────────────────────────

    /// <summary>
    /// P2 (format 1 - path:N): For any generated file path and line number in colon format,
    /// FindingsParser correctly extracts the path and line number.
    /// **Validates: Requirements 2.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public Property P2_FileLineExtraction_ColonFormat()
    {
        var gen =
            from filePath in GenFilePath()
            from lineNum in GenLineNumber()
            from marker in GenSeverityMarker()
            from separator in GenSeparator()
            from message in GenMessage()
            select (filePath, lineNum, $"{marker} {filePath}:{lineNum}{separator}{message}");

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (filePath, lineNum, input) = tuple;

            var findings = FindingsParser.Parse(input, "TestAgent");

            findings.Should().HaveCount(1);
            findings[0].FilePath.Should().Be(filePath);
            findings[0].LineNumber.Should().Be(lineNum);
        });
    }

    /// <summary>
    /// P2 (format 2 - path#LN): For any generated file path and line number in hash format,
    /// FindingsParser correctly extracts the path and line number.
    /// **Validates: Requirements 2.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public Property P2_FileLineExtraction_HashFormat()
    {
        var gen =
            from filePath in GenFilePath()
            from lineNum in GenLineNumber()
            from marker in GenSeverityMarker()
            from separator in GenSeparator()
            from message in GenMessage()
            select (filePath, lineNum, $"{marker} {filePath}#L{lineNum}{separator}{message}");

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (filePath, lineNum, input) = tuple;

            var findings = FindingsParser.Parse(input, "TestAgent");

            findings.Should().HaveCount(1);
            findings[0].FilePath.Should().Be(filePath);
            findings[0].LineNumber.Should().Be(lineNum);
        });
    }

    /// <summary>
    /// P2 (format 3 - path (line N)): For any generated file path and line number in paren format,
    /// FindingsParser correctly extracts the path and line number.
    /// **Validates: Requirements 2.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public Property P2_FileLineExtraction_ParenFormat()
    {
        var gen =
            from filePath in GenFilePath()
            from lineNum in GenLineNumber()
            from marker in GenSeverityMarker()
            from separator in GenSeparator()
            from message in GenMessage()
            select (filePath, lineNum, $"{marker} {filePath} (line {lineNum}){separator}{message}");

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (filePath, lineNum, input) = tuple;

            var findings = FindingsParser.Parse(input, "TestAgent");

            findings.Should().HaveCount(1);
            findings[0].FilePath.Should().Be(filePath);
            findings[0].LineNumber.Should().Be(lineNum);
        });
    }

    /// <summary>
    /// P2 (format 4 - path, line N): For any generated file path and line number in comma format,
    /// FindingsParser correctly extracts the path and line number.
    /// **Validates: Requirements 2.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public Property P2_FileLineExtraction_CommaFormat()
    {
        var gen =
            from filePath in GenFilePath()
            from lineNum in GenLineNumber()
            from marker in GenSeverityMarker()
            from separator in GenSeparator()
            from message in GenMessage()
            select (filePath, lineNum, $"{marker} {filePath}, line {lineNum}{separator}{message}");

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (filePath, lineNum, input) = tuple;

            var findings = FindingsParser.Parse(input, "TestAgent");

            findings.Should().HaveCount(1);
            findings[0].FilePath.Should().Be(filePath);
            findings[0].LineNumber.Should().Be(lineNum);
        });
    }

    // ─── P3: One Finding Per Line Invariant ─────────────────────────────────────

    /// <summary>
    /// P3: For any multi-line input, the number of findings returned by FindingsParser.Parse
    /// equals the number of input lines that contain at least one severity marker.
    /// **Validates: Requirements 2.8**
    /// </summary>
    [Property(MaxTest = 20)]
    public Property P3_OneFindingPerLine()
    {
        var genLineWithMarker =
            from marker in GenSeverityMarker()
            from message in GenMessage()
            select $"{marker} {message}";

        var genLineWithoutMarker =
            Gen.Elements(
                "This is a normal line",
                "No severity here",
                "Just some code: var x = 42;",
                "// comment line",
                "plain text without markers");

        var genLine = Gen.Frequency(
            (3, genLineWithMarker),
            (2, genLineWithoutMarker));

        var genLines =
            from count in Gen.Choose(2, 10)
            from lines in Gen.ArrayOf(genLine, count)
            select lines;

        return Prop.ForAll(genLines.ToArbitrary(), lines =>
        {
            var input = string.Join("\n", lines);
            var expectedCount = lines.Count(line =>
                line.Contains("[CRITICAL]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("[WARNING]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("[SUGGESTION]", StringComparison.OrdinalIgnoreCase));

            var findings = FindingsParser.Parse(input, "TestAgent");

            findings.Count.Should().Be(expectedCount);
        });
    }

    // ─── P9: Cross-Parser Invariant ─────────────────────────────────────────────

    /// <summary>
    /// P9: For any input, FindingsParser.Parse(input).Count ≤ SeverityParser.Parse(input.Split('\n')).Total
    /// because FindingsParser uses first marker per line, while SeverityParser counts all markers.
    /// **Validates: Requirements 14.1, 14.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public Property P9_CrossParserInvariant_FindingsCountLessOrEqualSeverityCount()
    {
        var genLineWithMultipleMarkers =
            from marker1 in GenSeverityMarker()
            from marker2 in GenSeverityMarker()
            from message in GenMessage()
            select $"{marker1} {message} {marker2} more text";

        var genLineWithSingleMarker =
            from marker in GenSeverityMarker()
            from message in GenMessage()
            select $"{marker} {message}";

        var genLineWithoutMarker =
            Gen.Elements("plain text", "no markers here", "var x = 1;");

        var genLine = Gen.Frequency(
            (2, genLineWithMultipleMarkers),
            (3, genLineWithSingleMarker),
            (2, genLineWithoutMarker));

        var genLines =
            from count in Gen.Choose(1, 8)
            from lines in Gen.ArrayOf(genLine, count)
            select lines;

        return Prop.ForAll(genLines.ToArbitrary(), lines =>
        {
            var input = string.Join("\n", lines);
            var inputLines = input.Split('\n');

            var findingsCount = FindingsParser.Parse(input, "TestAgent").Count;
            var severityCounts = SeverityParser.Parse(inputLines);
            var severityTotal = severityCounts.Critical + severityCounts.Warning + severityCounts.Suggestion;

            findingsCount.Should().BeLessThanOrEqualTo(severityTotal,
                "FindingsParser uses first marker per line, SeverityParser counts all markers");
        });
    }

    // ─── P12: Message Extraction with Separator Stripping ───────────────────────

    /// <summary>
    /// P12: For any finding produced by FindingsParser, the message does not start with
    /// separator characters (" — ", " - ", ": ") and is trimmed of whitespace.
    /// **Validates: Requirements 2.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public Property P12_MessageExtraction_NoLeadingSeparators()
    {
        var gen =
            from marker in GenSeverityMarker()
            from filePath in GenFilePath()
            from lineNum in GenLineNumber()
            from separator in GenSeparator()
            from message in GenMessage()
            select $"{marker} {filePath}:{lineNum}{separator}{message}";

        return Prop.ForAll(gen.ToArbitrary(), input =>
        {
            var findings = FindingsParser.Parse(input, "TestAgent");

            findings.Should().HaveCount(1);
            var msg = findings[0].Message;

            // Message should not start with separator characters
            msg.Should().NotStartWith(" — ");
            msg.Should().NotStartWith(" - ");
            msg.Should().NotStartWith(": ");

            // Message should be trimmed of whitespace
            msg.Should().Be(msg.Trim());
        });
    }

    /// <summary>
    /// P12 (without file reference): For findings without file:line, the message is still
    /// properly extracted with separators stripped and whitespace trimmed.
    /// **Validates: Requirements 2.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public Property P12_MessageExtraction_WithoutFileRef_NoLeadingSeparators()
    {
        var gen =
            from marker in GenSeverityMarker()
            from separator in GenSeparator()
            from message in GenMessage()
            select $"{marker}{separator}{message}";

        return Prop.ForAll(gen.ToArbitrary(), input =>
        {
            var findings = FindingsParser.Parse(input, "TestAgent");

            findings.Should().HaveCount(1);
            var msg = findings[0].Message;

            // Message should not start with separator characters
            msg.Should().NotStartWith(" — ");
            msg.Should().NotStartWith(" - ");
            msg.Should().NotStartWith(": ");

            // Message should be trimmed of whitespace
            msg.Should().Be(msg.Trim());

            // Message should not be empty (we generated non-empty messages)
            msg.Should().NotBeEmpty();
        });
    }
}
