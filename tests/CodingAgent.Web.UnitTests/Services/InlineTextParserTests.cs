using AwesomeAssertions;
using CodingAgent.Web.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// <see cref="InlineTextParser"/>: the two marks the triage page renders, code spans and bold. Agent text
/// is untrusted, so the parser only ever returns plain strings; building elements from them is the
/// component's job.
/// </summary>
public class InlineTextParserTests
{
    private static InlineToken Text(string text) => new(InlineKind.Text, text);
    private static InlineToken Code(string text) => new(InlineKind.Code, text);
    private static InlineToken Bold(string text) => new(InlineKind.Bold, text);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmpty_GivesNoTokens(string? text) =>
        InlineTextParser.Parse(text).Should().BeEmpty();

    [Fact]
    public void PlainText_IsOneTextToken() =>
        InlineTextParser.Parse("The worker acks first.").Should().Equal(Text("The worker acks first."));

    [Fact]
    public void CodeSpan_GivesCode() =>
        InlineTextParser.Parse("`x`").Should().Equal(Code("x"));

    [Fact]
    public void BoldSpan_GivesBold() =>
        InlineTextParser.Parse("**x**").Should().Equal(Bold("x"));

    [Fact]
    public void MarksInsideText_SplitItIntoTokens() =>
        InlineTextParser.Parse("Fix `RefactoringExecutor` and **retry** it").Should().Equal(
            Text("Fix "), Code("RefactoringExecutor"), Text(" and "), Bold("retry"), Text(" it"));

    [Fact]
    public void AdjacentSpans_HaveNoEmptyTextBetweenThem() =>
        InlineTextParser.Parse("`a`**b**`c`").Should().Equal(Code("a"), Bold("b"), Code("c"));

    [Theory]
    [InlineData("a `b")]
    [InlineData("a **b")]
    [InlineData("a * b")]
    [InlineData("`")]
    [InlineData("**")]
    public void MarkWithoutClosingPartner_StaysLiteralInOneTextToken(string input) =>
        InlineTextParser.Parse(input).Should().Equal(Text(input));

    [Fact]
    public void AnUnclosedMarkAfterASpan_StaysLiteralInTheTextAfterIt() =>
        InlineTextParser.Parse("a `b` c `d").Should().Equal(Text("a "), Code("b"), Text(" c `d"));

    [Theory]
    [InlineData("****")]
    [InlineData("``")]
    [InlineData("a **** b")]
    [InlineData("a `` b")]
    public void EmptyPair_StaysLiteral(string input) =>
        InlineTextParser.Parse(input).Should().Equal(Text(input));

    [Fact]
    public void EmptyPairFollowedByASpan_SkipsBothMarksOfThePair() =>
        InlineTextParser.Parse("****x**").Should().Equal(Text("****x**"));

    [Fact]
    public void InsideACodeSpan_NothingIsParsed() =>
        InlineTextParser.Parse("`**x**`").Should().Equal(Code("**x**"));

    [Fact]
    public void BoldContent_IsNotParsedFurther() =>
        InlineTextParser.Parse("**a `b` c**").Should().Equal(Bold("a `b` c"));

    [Fact]
    public void TheFirstMarkWins_WhenSpansWouldOverlap() =>
        InlineTextParser.Parse("`a **b` c**").Should().Equal(Code("a **b"), Text(" c**"));

    [Theory]
    [InlineData("`a\nb`")]
    [InlineData("`a\r\nb`")]
    [InlineData("`a\rb`")]
    [InlineData("**a\nb**")]
    [InlineData("**a\r\nb**")]
    [InlineData("**a\rb**")]
    public void AMark_DoesNotSpanANewline(string input) =>
        InlineTextParser.Parse(input).Should().Equal(Text(input));

    [Fact]
    public void SpansOnSeparateLines_AreEachParsed_AndTheNewlineStaysText() =>
        InlineTextParser.Parse("`a`\n**b**").Should().Equal(Code("a"), Text("\n"), Bold("b"));

    [Fact]
    public void AnUnclosedMarkOnOneLine_DoesNotPairWithAMarkOnTheNext() =>
        InlineTextParser.Parse("`a\n`b`").Should().Equal(Text("`a\n"), Code("b"));

    [Fact]
    public void WhitespaceInsideASpan_IsKept() =>
        InlineTextParser.Parse("` x `").Should().Equal(Code(" x "));

    [Theory]
    [InlineData("*x*")]
    [InlineData("_x_")]
    [InlineData("__x__")]
    [InlineData("# heading")]
    [InlineData("[link](https://example.com)")]
    [InlineData("![img](https://example.com/a.png)")]
    [InlineData("- item")]
    public void OtherMarkdown_StaysText(string input) =>
        InlineTextParser.Parse(input).Should().Equal(Text(input));

    [Fact]
    public void HtmlInAgentText_StaysPlainText() =>
        InlineTextParser.Parse("<script>alert(1)</script> `<b>x</b>`").Should().Equal(
            Text("<script>alert(1)</script> "), Code("<b>x</b>"));

    [Fact]
    public void ThreeStars_GiveBoldThatKeepsTheLeftOverStar() =>
        InlineTextParser.Parse("***x***").Should().Equal(Bold("*x"), Text("*"));

    // ── Properties ───────────────────────────────────────────────────────────

    [Property(MaxTest = 20, Arbitrary = new[] { typeof(MarkedTextArbitraries) })]
    public void Tokens_JoinedBackWithTheirMarks_GiveTheInput(MarkedText input)
    {
        var rebuilt = string.Concat(InlineTextParser.Parse(input.Value).Select(Serialize));

        rebuilt.Should().Be(input.Value);
    }

    [Property(MaxTest = 20, Arbitrary = new[] { typeof(MarkedTextArbitraries) })]
    public void Tokens_NeverAreEmpty_NorTwoTextsInARow_NorASpanAcrossALine(MarkedText input)
    {
        var tokens = InlineTextParser.Parse(input.Value);

        tokens.Should().OnlyContain(t => t.Text.Length > 0);
        for (var i = 1; i < tokens.Count; i++)
        {
            (tokens[i].Kind == InlineKind.Text && tokens[i - 1].Kind == InlineKind.Text).Should().BeFalse("neighbouring text is one token");
        }
        tokens.Where(t => t.Kind != InlineKind.Text).Should().OnlyContain(t => !t.Text.Contains('\n') && !t.Text.Contains('\r'));
    }

    private static string Serialize(InlineToken token) => token.Kind switch
    {
        InlineKind.Code => $"`{token.Text}`",
        InlineKind.Bold => $"**{token.Text}**",
        _ => token.Text,
    };
}

/// <summary>A string drawn from an alphabet that is mostly marks and line breaks, so spans actually occur.</summary>
public sealed record MarkedText(string Value)
{
    public override string ToString() => Value.Replace("\n", "\\n").Replace("\r", "\\r");
}

public static class MarkedTextArbitraries
{
    public static Arbitrary<MarkedText> MarkedTextArb()
    {
        var alphabet = Gen.Elements('`', '*', '\n', '\r', 'a', 'b', ' ', '<');
        return Gen.Choose(0, 24)
            .SelectMany(length => Gen.ArrayOf(alphabet, length))
            .Select(chars => new MarkedText(new string(chars)))
            .ToArbitrary();
    }
}
