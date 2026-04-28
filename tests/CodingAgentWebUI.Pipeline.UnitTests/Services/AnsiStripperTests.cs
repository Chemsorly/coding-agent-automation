using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="AnsiStripper"/>.
/// </summary>
public class AnsiStripperTests
{
    [Fact]
    public void Strip_PlainText_ReturnsUnchanged()
    {
        AnsiStripper.Strip("Hello, World!").Should().Be("Hello, World!");
    }

    [Fact]
    public void Strip_EmptyString_ReturnsEmpty()
    {
        AnsiStripper.Strip("").Should().Be("");
    }

    [Fact]
    public void Strip_ColorCodes_RemovesEscapeSequences()
    {
        // ESC[32m = green, ESC[0m = reset
        var input = "\x1B[32mSuccess\x1B[0m";
        AnsiStripper.Strip(input).Should().Be("Success");
    }

    [Fact]
    public void Strip_BoldAndReset_RemovesSequences()
    {
        // ESC[1m = bold, ESC[0m = reset
        var input = "\x1B[1mBold text\x1B[0m";
        AnsiStripper.Strip(input).Should().Be("Bold text");
    }

    [Fact]
    public void Strip_MultipleColorCodes_RemovesAll()
    {
        var input = "\x1B[31mError:\x1B[0m \x1B[33mWarning\x1B[0m text";
        AnsiStripper.Strip(input).Should().Be("Error: Warning text");
    }

    [Fact]
    public void Strip_CursorMovement_RemovesSequences()
    {
        // ESC[2A = move cursor up 2 lines
        var input = "Line1\x1B[2ALine2";
        AnsiStripper.Strip(input).Should().Be("Line1Line2");
    }

    [Fact]
    public void Strip_ClearLine_RemovesSequence()
    {
        // [K = clear to end of line (bare bracket form)
        var input = "Progress: 50%[K";
        AnsiStripper.Strip(input).Should().Be("Progress: 50%");
    }

    [Fact]
    public void Strip_BareBracketSequences_RemovesSequences()
    {
        // Some CLI tools emit [32m without the ESC prefix
        var input = "[32mGreen text[0m";
        AnsiStripper.Strip(input).Should().Be("Green text");
    }

    [Fact]
    public void Strip_OscSequences_RemovesSequences()
    {
        // ESC]...BEL = Operating System Command (e.g., terminal title)
        var input = "\x1B]0;My Title\x07Normal text";
        AnsiStripper.Strip(input).Should().Be("Normal text");
    }

    [Fact]
    public void Strip_ComplexSequences_RemovesAll()
    {
        // ESC[38;5;196m = 256-color red
        var input = "\x1B[38;5;196mRed\x1B[0m normal";
        AnsiStripper.Strip(input).Should().Be("Red normal");
    }

    [Fact]
    public void Strip_PreservesNewlines()
    {
        var input = "\x1B[32mLine1\x1B[0m\nLine2\n\x1B[31mLine3\x1B[0m";
        AnsiStripper.Strip(input).Should().Be("Line1\nLine2\nLine3");
    }

    [Fact]
    public void Strip_PreservesSpaces()
    {
        var input = "  \x1B[1mindented\x1B[0m  ";
        AnsiStripper.Strip(input).Should().Be("  indented  ");
    }

    [Fact]
    public void Strip_RealWorldKiroOutput_CleansCorrectly()
    {
        // Simulated Kiro CLI output with progress indicators
        var input = "\x1B[36m⠋\x1B[0m Thinking...";
        var result = AnsiStripper.Strip(input);
        result.Should().Contain("Thinking...");
        result.Should().NotContain("\x1B");
    }
}
