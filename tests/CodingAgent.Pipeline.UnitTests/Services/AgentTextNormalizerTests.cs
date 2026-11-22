using System.Diagnostics;
using System.Globalization;
using System.Text;
using AwesomeAssertions;
using CodingAgent.Pipeline.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="AgentTextNormalizer"/>: what it removes from text by authors outside the repository,
/// and what it must leave alone.
/// </summary>
public class AgentTextNormalizerTests
{
    public static TheoryData<string, string?, string> Rows => new()
    {
        { "null", null, "" },
        { "empty", "", "" },
        { "plain text with CRLF, umlaut, astral emoji", "Grüße \U0001F680\r\n\tline `code`", "Grüße \U0001F680\r\n\tline `code`" },
        { "inline comment", "a <!-- b --> c", "a  c" },
        { "multi-line comment", "a\n<!-- b\nc -->\nd", "a\n\nd" },
        { "CRLF comment", "a\r\n<!-- b -->\r\nc", "a\r\n\r\nc" },
        { "pipeline marker in unverified text", "<!-- agent:gate-rejection -->\nreal", "\nreal" },
        { "unterminated at line start hides the rest", "a\n<!-- b\nc", "a\n" },
        { "unterminated indented up to 3 spaces", "a\n   <!-- b\nc", "a\n   " },
        { "unterminated in mid-line is visible text", "fails on `<!--` here\nnext", "fails on `<!--` here\nnext" },
        { "empty comment forms", "x <!--> y <!---> z", "x  y  z" },
        { "fenced code keeps its comment", "```html\n<!-- keep -->\n```\nafter <!-- drop --> end", "```html\n<!-- keep -->\n```\nafter  end" },
        { "tilde fence needs a closer at least as long", "~~~~\n<!-- keep -->\n~~~\n<!-- keep too -->\n~~~~\n<!-- drop -->", "~~~~\n<!-- keep -->\n~~~\n<!-- keep too -->\n~~~~\n" },
        { "CRLF fence", "```\r\n<!-- keep -->\r\n```\r\n<!-- drop -->x", "```\r\n<!-- keep -->\r\n```\r\nx" },
        { "unterminated fence runs to the end", "```\n<!-- keep -->", "```\n<!-- keep -->" },
        { "unterminated opener inside a fence", "```\n<!-- keep\n```\nrest", "```\n<!-- keep\n```\nrest" },
        { "indented fence is not trusted", "  ```\n<!-- drop -->\n  ```", "  ```\n\n  ```" },
        { "fence in a list item is not trusted", "- ```\n  x\n  ```\n<!-- drop -->\nend", "- ```\n  x\n  ```\n\nend" },
        { "list item ends without a closing fence", "- a\n  ```\n  x\n- b\n<!-- drop -->\nend", "- a\n  ```\n  x\n- b\n\nend" },
        { "backtick in the info string is no fence", "``` a`b\n<!-- drop -->", "``` a`b\n" },
        { "comment containing a fence line", "<!-- \n```\n -->\n<!-- drop -->x", "\nx" },
        { "fence text right after a comment is no fence", "<!-- a -->```\n<!-- drop -->", "```\n" },
        { "reference definition idiom", "a\n[//]: # (hidden)\nb", "a\nb" },
        { "reference definition with url and title", "[x]: https://e.example \"t\"\nb", "b" },
        { "reference definition, title on next lines", "[x]: /u\n  'hidden\n  more'\nb", "b" },
        { "reference definition, destination on next line", "[x]:\n  /u\nb", "b" },
        { "reference definition with CRLF", "[x]: /u\r\nb", "b" },
        { "reference definition at the end", "b\n[x]: /u", "b\n" },
        { "next line is not a title", "[x]: /u\n\"quoted\" sentence", "\"quoted\" sentence" },
        { "footnote stays", "[^1]: visible note", "[^1]: visible note" },
        { "bracketed label with prose stays", "[Bug]: crash on start", "[Bug]: crash on start" },
        { "reference definition in a fence stays", "```\n[x]: /u\n```", "```\n[x]: /u\n```" },
        { "ZWJ emoji falls apart into its emoji", "\U0001F468\u200D\U0001F469\u200D\U0001F467", "\U0001F468\U0001F469\U0001F467" },
        { "variation selector 16", "\u26A0\uFE0F x", "\u26A0 x" },
        { "format characters", "a\u200Bb\u2060c\u202Ed\uFEFFe\u00ADf", "abcdef" },
        { "astral Tags block", "a\U000E0041\U000E0042\U000E007Fb", "ab" },
        { "unassigned code point in the Tags block", "a\U000E0002b", "ab" },
        { "supplementary variation selector", "a\U000E0100b", "ab" },
        { "Mongolian variation selector", "a\u180Bb", "ab" },
        { "format character inside a comment opener", "<\u200B!-- x -->y", "y" },
        { "format characters are removed in fences too", "```\na\u200Bb\n```", "```\nab\n```" },
    };

    [Theory]
    [MemberData(nameof(Rows), DisableDiscoveryEnumeration = true)]
    public void Normalize_RemovesWhatThePageHides(string name, string? input, string expected) =>
        AgentTextNormalizer.Normalize(input).Should().Be(expected, name);

    [Fact]
    public void Normalize_LoneSurrogate_BecomesReplacementCharacter() =>
        AgentTextNormalizer.Normalize("a\uD83Db").Should().Be("a�b");

    [Fact]
    public void Normalize_ManyUnterminatedOpeners_StaysLinear()
    {
        // One 1.2 MB line, the size a provider allows. A quadratic scan needs about a minute for it, a linear one well under a second.
        var input = string.Concat(Enumerable.Repeat("x <!--", 200_000));

        var stopwatch = Stopwatch.StartNew();
        var output = AgentTextNormalizer.Normalize(input);
        stopwatch.Stop();

        output.Should().Be(input);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Normalize_ManyDefinitionLookalikes_StaysLinear()
    {
        var input = string.Concat(Enumerable.Repeat("[a]: b \"c\n[a]: b 'c\n[a]: b (c\n", 15_000));

        var stopwatch = Stopwatch.StartNew();
        var output = AgentTextNormalizer.Normalize(input);
        stopwatch.Stop();

        output.Should().Be(input);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    // FsCheck's default string generator only produces ASCII, which has no format characters, so the property needs
    // its own: ASCII and Markdown pieces mixed with the characters the normalizer has to remove.
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(InvisibleCharacterArbitraries) })]
    public void Normalize_NeverLeavesAFormatCharacter(string input)
    {
        var output = AgentTextNormalizer.Normalize(input);

        output.EnumerateRunes()
            .Where(rune => Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
                           || rune.Value is >= 0xE0000 and <= 0xE007F
                           || rune.Value is >= 0xFE00 and <= 0xFE0F
                           || rune.Value is >= 0xE0100 and <= 0xE01EF
                           || rune.Value is >= 0x180B and <= 0x180F)
            .Should().BeEmpty();
    }

    [Fact]
    public void InvisibleCharacterArbitraries_ReachTheCharactersTheNormalizerRemoves()
    {
        // Guards the property above: it only proves something while its generator really produces invisible characters.
        var generated = Gen.Sample(InvisibleCharacterArbitraries.Strings().Generator, 200)
            .SelectMany(text => text.EnumerateRunes())
            .ToList();

        generated.Any(rune => Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format).Should().BeTrue();
        generated.Any(rune => rune.Value is >= 0xE0000 and <= 0xE007F).Should().BeTrue();
        generated.Any(rune => rune.Value is >= 0xFE00 and <= 0xFE0F).Should().BeTrue();
    }
}

/// <summary>Strings that mix visible text, Markdown pieces and invisible characters.</summary>
public static class InvisibleCharacterArbitraries
{
    private static readonly string[] Pieces =
    [
        "a", "Z", " ", "\n", "\r\n", "<!--", "-->", "```", "[x]: /u\n", "\U0001F680",
        "\u00AD", "\u200B", "\u200D", "\u2060", "\u202E", "\uFEFF",
        "\U000E0041", "\U000E007F", "\uFE0F", "\U000E0100", "\u180B"
    ];

    public static Arbitrary<string> Strings() =>
        Gen.ArrayOf(Gen.Elements(Pieces)).Select(pieces => string.Concat(pieces)).ToArbitrary();
}
