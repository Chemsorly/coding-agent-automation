using AwesomeAssertions;
using CodingAgent.Web.Auth;

namespace CodingAgent.Web.UnitTests.Auth;

public class SessionDurationParserTests
{
    [Theory]
    [InlineData("12h", 12 * 60)]
    [InlineData("48h", 48 * 60)]
    [InlineData("90m", 90)]
    [InlineData(" 8h ", 8 * 60)]
    [InlineData("1.00:00:00", 24 * 60)]
    [InlineData("02:30:00", 150)]
    public void ValidValues_Parse(string value, int expectedMinutes)
    {
        SessionDurationParser.TryParse(value, out var duration).Should().BeTrue();
        duration.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12")]
    [InlineData("0h")]
    [InlineData("-5m")]
    [InlineData("h")]
    [InlineData("12d")]
    [InlineData("1.5h")]
    [InlineData("00:00:00")]
    [InlineData("abc")]
    public void InvalidValues_DoNotParse(string? value)
    {
        SessionDurationParser.TryParse(value, out _).Should().BeFalse();
    }
}
