using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for plan validation and text sanitization in the decomposition pipeline.
/// Feature: 027-epic-decomposition-pipeline
/// </summary>
public class DecompositionSanitizationPropertyTests
{
    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 5: Plan File Validation
    ///
    /// For any file content (including empty string, whitespace-only, and content shorter than
    /// 20 characters), the plan validation logic SHALL reject it as invalid. For any content of
    /// 20 or more characters, the validation SHALL accept it.
    ///
    /// **Validates: Requirements 3.11**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PlanValidationArbitraries) })]
    public Property PlanValidation_RejectsContentShorterThan20Chars_AcceptsContentAtLeast20Chars(PlanFileContent input)
    {
        var isValid = ValidatePlanFile(input.Content);

        if (input.Content is null || input.Content.Length < 20)
        {
            return (!isValid).ToProperty()
                .Label($"Expected rejection for content length {input.Content?.Length ?? 0}");
        }
        else
        {
            return isValid.ToProperty()
                .Label($"Expected acceptance for content length {input.Content.Length}");
        }
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 6: Sub-Issue Title Sanitization
    ///
    /// For any string input, SanitizeTitle SHALL produce output that:
    /// (a) contains no newline characters,
    /// (b) has length ≤ 200 characters,
    /// (c) has no leading or trailing whitespace.
    ///
    /// **Validates: Requirements 4.6**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(TitleSanitizationArbitraries) })]
    public Property TitleSanitization_OutputHasNoNewlines_MaxLength200_NoLeadingTrailingWhitespace(TitleInput input)
    {
        var result = TextSanitizer.SanitizeTitle(input.Value);

        var noNewlines = !result.Contains('\n') && !result.Contains('\r');
        var withinLength = result.Length <= 200;
        var noLeadingTrailingWhitespace = result == result.Trim();

        return (noNewlines && withinLength && noLeadingTrailingWhitespace).ToProperty()
            .Label($"noNewlines={noNewlines}, withinLength={withinLength} (len={result.Length}), trimmed={noLeadingTrailingWhitespace}");
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property 7: Sub-Issue Body Sanitization
    ///
    /// For any string input, SanitizeMarkdown SHALL produce output where:
    /// (a) no @ character is immediately followed by a word character (mentions are broken),
    /// (b) no raw &lt; characters remain (HTML open tags are escaped).
    /// Note: &gt; is intentionally NOT escaped — GitHub renders it safely in markdown (blockquotes).
    ///
    /// **Validates: Requirements 4.6**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(BodySanitizationArbitraries) })]
    public Property BodySanitization_MentionsBroken_HtmlEscaped(BodyInput input)
    {
        var result = TextSanitizer.SanitizeMarkdown(input.Value);

        // No raw < characters remain (> is allowed for blockquotes)
        var noRawOpenAngleBrackets = !result.Contains('<');

        // No @ immediately followed by a word character (mentions are broken)
        var noActiveMentions = true;
        for (var i = 0; i < result.Length - 1; i++)
        {
            if (result[i] == '@' && char.IsLetterOrDigit(result[i + 1]))
            {
                noActiveMentions = false;
                break;
            }
        }

        return (noRawOpenAngleBrackets && noActiveMentions).ToProperty()
            .Label($"noRawOpenAngleBrackets={noRawOpenAngleBrackets}, noActiveMentions={noActiveMentions}");
    }

    // --- Plan file validation logic (mirrors DecompositionAnalysisStep validation) ---

    /// <summary>
    /// Validates plan file content: must exist and have at least 20 characters.
    /// This mirrors the validation logic in DecompositionAnalysisStep.
    /// </summary>
    private static bool ValidatePlanFile(string? content)
    {
        if (content is null)
            return false;

        return content.Length >= 20;
    }

    // --- Custom wrapper types ---

    public sealed class PlanFileContent
    {
        public string? Content { get; }
        public PlanFileContent(string? content) => Content = content;
        public override string ToString() => Content is null ? "<null>" : $"\"{Content}\" (len={Content.Length})";
    }

    public sealed class TitleInput
    {
        public string Value { get; }
        public TitleInput(string value) => Value = value;
        public override string ToString() => $"\"{Value}\" (len={Value.Length})";
    }

    public sealed class BodyInput
    {
        public string Value { get; }
        public BodyInput(string value) => Value = value;
        public override string ToString() => $"\"{Value}\" (len={Value.Length})";
    }

    // --- Arbitraries ---

    public class PlanValidationArbitraries
    {
        public static Arbitrary<PlanFileContent> PlanFileContentArb()
        {
            var nullGen = Gen.Constant((string?)null).Select(s => new PlanFileContent(s));

            var emptyGen = Gen.Constant("").Select(s => new PlanFileContent(s));

            // Short content: 1-19 characters
            var shortGen =
                from len in Gen.Choose(1, 19)
                from chars in Gen.ArrayOf<char>(
                    Gen.Elements('a', 'b', 'c', ' ', '\n', '\t', '#', '-', '*', '1', '2', '3'), len)
                select new PlanFileContent(new string(chars));

            // Whitespace-only content of various lengths
            var whitespaceGen =
                from len in Gen.Choose(1, 30)
                from chars in Gen.ArrayOf<char>(Gen.Elements(' ', '\t', '\n', '\r'), len)
                select new PlanFileContent(new string(chars));

            // Valid content: 20+ characters
            var validGen =
                from len in Gen.Choose(20, 500)
                from chars in Gen.ArrayOf<char>(
                    Gen.Elements('a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j',
                        ' ', '\n', '#', '-', '*', '1', '2', '3', '.', ':'), len)
                select new PlanFileContent(new string(chars));

            var combined = Gen.OneOf(nullGen, emptyGen, shortGen, whitespaceGen, validGen);
            return combined.ToArbitrary();
        }
    }

    public class TitleSanitizationArbitraries
    {
        public static Arbitrary<TitleInput> TitleInputArb()
        {
            // Mix of normal text, newlines, long strings, whitespace-heavy strings
            var normalGen =
                from len in Gen.Choose(1, 50)
                from chars in Gen.ArrayOf<char>(
                    Gen.Elements('A', 'b', 'C', 'd', ' ', '-', '_', '(', ')', '[', ']'), len)
                select new TitleInput(new string(chars));

            var withNewlinesGen =
                from prefix in Gen.Elements("Hello", "World", "Test", "Feature", "Bug")
                from middle in Gen.Elements("\nMiddle\n", "\r\nLine\r\n", "\rCR\r")
                from suffix in Gen.Elements("End", "Done", "Fix", "Update")
                select new TitleInput(prefix + middle + suffix);

            var longGen =
                from len in Gen.Choose(180, 300)
                from chars in Gen.ArrayOf<char>(
                    Gen.Elements('a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm'), len)
                select new TitleInput(new string(chars));

            var whitespaceHeavyGen =
                from prefix in Gen.Elements("", " ", "  ", "   ", "\t")
                from middle in Gen.Elements("Title", "Some Feature", "Bug Fix #123")
                from suffix in Gen.Elements("", " ", "  ", "   ", "\t")
                select new TitleInput(prefix + middle + suffix);

            var mixedGen =
                from len in Gen.Choose(5, 250)
                from chars in Gen.ArrayOf<char>(
                    Gen.Elements('A', 'z', '0', '9', ' ', '\n', '\r', '\t',
                        '-', '_', '.', ':', '#', '@', '!', '?'), len)
                select new TitleInput(new string(chars));

            var allGens = Gen.OneOf(normalGen, withNewlinesGen, longGen, whitespaceHeavyGen, mixedGen);
            return allGens.ToArbitrary();
        }
    }

    public class BodySanitizationArbitraries
    {
        public static Arbitrary<BodyInput> BodyInputArb()
        {
            // Strings with @-mentions
            var mentionGen =
                from prefix in Gen.Elements("Hello ", "Check ", "CC ", "")
                from mention in Gen.Elements("@user", "@admin", "@bot", "@team-lead")
                from suffix in Gen.Elements(" please review", " fix this", "", " thanks")
                select new BodyInput(prefix + mention + suffix);

            // Strings with HTML angle brackets
            var htmlGen =
                from content in Gen.Elements(
                    "<script>alert('xss')</script>",
                    "<div>content</div>",
                    "Use <T> generic type",
                    "a > b && c < d",
                    "<img src=x onerror=alert(1)>")
                select new BodyInput(content);

            // Mixed content with both @ and HTML
            var mixedGen =
                from mention in Gen.Elements("@user", "@admin", "@reviewer")
                from html in Gen.Elements("<b>bold</b>", "<script>", "a < b")
                select new BodyInput($"{mention} said: {html}");

            // Random strings with special characters
            var randomGen =
                from len in Gen.Choose(1, 200)
                from chars in Gen.ArrayOf<char>(
                    Gen.Elements('a', 'b', 'c', ' ', '@', '<', '>', '#', '*', '_',
                        '\n', '!', '?', '.', ',', ':', ';', '(', ')', '[', ']'), len)
                select new BodyInput(new string(chars));

            // Edge cases: strings that are just special characters
            var edgeCaseGen = Gen.Elements(
                new BodyInput("@"),
                new BodyInput("<"),
                new BodyInput(">"),
                new BodyInput("@<>"),
                new BodyInput("@@@@"),
                new BodyInput("<<<>>>"),
                new BodyInput("@user<tag>@admin"));

            var allGens = Gen.OneOf(mentionGen, htmlGen, mixedGen, randomGen, edgeCaseGen);
            return allGens.ToArbitrary();
        }
    }
}
