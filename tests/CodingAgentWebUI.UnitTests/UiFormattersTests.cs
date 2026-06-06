using AwesomeAssertions;
using CodingAgentWebUI;

namespace CodingAgentWebUI.UnitTests;

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
}
