using AwesomeAssertions;
using CodingAgent.Web;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.UnitTests;

public class UiFormattersTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Truncate_NullOrEmpty_ReturnsEmpty(string? value)
    {
        UiFormatters.Truncate(value, 10).Should().Be(string.Empty);
    }

    [Fact]
    public void Truncate_ShortString_ReturnsUnchanged()
    {
        UiFormatters.Truncate("hello", 10).Should().Be("hello");
    }

    [Fact]
    public void Truncate_ExactLength_ReturnsUnchanged()
    {
        UiFormatters.Truncate("1234567890", 10).Should().Be("1234567890");
    }

    [Fact]
    public void Truncate_LongString_TruncatesWithEllipsis()
    {
        UiFormatters.Truncate("12345678901", 10).Should().Be("1234567...");
    }

    [Fact]
    public void Truncate_OutputLength_EqualsMaxLength()
    {
        var result = UiFormatters.Truncate("This is a long string that should be truncated", 20);
        result.Length.Should().Be(20);
    }

    [Fact]
    public void TruncateUnicode_ShortString_ReturnsUnchanged()
    {
        UiFormatters.TruncateUnicode("hello", 10).Should().Be("hello");
    }

    [Fact]
    public void TruncateUnicode_ExactLength_ReturnsUnchanged()
    {
        UiFormatters.TruncateUnicode("1234567890", 10).Should().Be("1234567890");
    }

    [Fact]
    public void TruncateUnicode_LongString_TruncatesWithUnicodeEllipsis()
    {
        UiFormatters.TruncateUnicode("12345678901", 10).Should().Be("1234567890…");
    }

    [Fact]
    public void TruncateUnicode_OutputLength_IsMaxLengthPlusOne()
    {
        var result = UiFormatters.TruncateUnicode("This is a long string", 10);
        result.Length.Should().Be(11); // maxLength chars + 1 Unicode ellipsis
    }

    [Fact]
    public void FormatTimeAgo_Seconds()
    {
        var timestamp = DateTimeOffset.UtcNow.AddSeconds(-30);
        UiFormatters.FormatTimeAgo(timestamp).Should().Be("30s ago");
    }

    [Fact]
    public void FormatTimeAgo_Minutes()
    {
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        UiFormatters.FormatTimeAgo(timestamp).Should().Be("5m ago");
    }

    [Fact]
    public void FormatTimeAgo_Hours()
    {
        var timestamp = DateTimeOffset.UtcNow.AddHours(-3);
        UiFormatters.FormatTimeAgo(timestamp).Should().Be("3h ago");
    }

    [Fact]
    public void FormatTimeAgo_Days()
    {
        var timestamp = DateTimeOffset.UtcNow.AddDays(-2);
        UiFormatters.FormatTimeAgo(timestamp).Should().Be("2d ago");
    }

    [Fact]
    public void FormatRunType_Consolidation_ReturnsConsolidationString()
    {
        // Acceptance criterion: FormatRunType(PipelineRunType.Consolidation) must NOT return "Implementation"
        var result = UiFormatters.FormatRunType(PipelineRunType.Consolidation);
        result.Should().Be("Consolidation",
            because: "Consolidation is a distinct run type and must not fall through to the Implementation default");
    }

    // ── StripMarkdown tests (AC6: drawer preview renders plain text) ──────────

    [Fact]
    public void StripMarkdown_BoldAsterisks_Stripped()
    {
        UiFormatters.StripMarkdown("**bold** text").Should().Be("bold text");
    }

    [Fact]
    public void StripMarkdown_ItalicAsterisks_Stripped()
    {
        UiFormatters.StripMarkdown("*italic* text").Should().Be("italic text");
    }

    [Fact]
    public void StripMarkdown_InlineCode_Stripped()
    {
        UiFormatters.StripMarkdown("`code` span").Should().Be("code span");
    }

    [Fact]
    public void StripMarkdown_BlockquotePrefix_Stripped()
    {
        UiFormatters.StripMarkdown("> blockquote").Should().Be("blockquote");
    }

    [Fact]
    public void StripMarkdown_UnorderedListHyphen_Stripped()
    {
        UiFormatters.StripMarkdown("- list item").Should().Be("list item");
    }

    [Fact]
    public void StripMarkdown_UnorderedListAsterisk_Stripped()
    {
        // TODO [WARNING]: The interaction between the italic-strip pass and a line starting with
        // "* " followed by text containing "*emphasis*" is untested. The pass order (bold → italic
        // → code → blockquote → list) is load-bearing: italic runs first, so "*word* rest" becomes
        // "word rest" before the list-marker pass fires. If the order were reversed the list-marker
        // pass would incorrectly treat "*word*" as a list marker. Add a test covering
        // "* item with *emphasis*" → "item with emphasis" to pin this ordering dependency and
        // prevent a silent regression if the passes are ever reordered.
        UiFormatters.StripMarkdown("* list item").Should().Be("list item");
    }

    [Fact]
    public void StripMarkdown_Url_Preserved()
    {
        // TODO [WARNING]: This test input contains no markdown metacharacters (no *, `, >), so it
        // would pass even if the regexes were wildly over-eager. A meaningful URL-preservation
        // test would use a URL that overlaps with markdown syntax, e.g. a URL containing asterisks
        // in its path or a Markdown link "[text](url)", to prove stripping does not corrupt it.
        // URLs must NOT be stripped — https:// contains no markdown syntax
        UiFormatters.StripMarkdown("See https://example.com/path for details").Should()
            .Be("See https://example.com/path for details");
    }

    [Fact]
    public void StripMarkdown_PlainText_Unchanged()
    {
        UiFormatters.StripMarkdown("just plain text").Should().Be("just plain text");
    }

    [Fact]
    public void StripMarkdown_Empty_ReturnsEmpty()
    {
        // TODO [WARNING]: Null input is not covered. The implementation returns early via
        // string.IsNullOrEmpty (returning null for a null input) despite the method signature
        // being non-nullable string. Per project convention (see Truncate_NullOrEmpty_ReturnsEmpty),
        // both null and empty should be tested. Add a StripMarkdown_Null_ReturnsEmpty test to
        // match the convention and to clarify the expected behaviour for null callers.
        UiFormatters.StripMarkdown("").Should().Be("");
    }

    [Fact]
    public void StripMarkdown_Combined_Stripped()
    {
        UiFormatters.StripMarkdown("**bold** and `code` and *italic*").Should().Be("bold and code and italic");
    }
}
