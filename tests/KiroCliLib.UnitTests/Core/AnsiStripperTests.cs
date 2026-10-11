using KiroCliLib.Core;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Unit tests for AnsiStripper.
/// Validates stripping of ANSI escape codes from terminal output.
/// </summary>
public class AnsiStripperTests
{
    [Fact]
    public void Strip_PlainText_ReturnsUnchanged()
    {
        var input = "Hello, world!";
        Assert.Equal(input, AnsiStripper.Strip(input));
    }

    [Fact]
    public void Strip_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", AnsiStripper.Strip(""));
    }

    [Theory]
    [InlineData("\x1B[31mError\x1B[0m", "Error")]
    [InlineData("\x1B[1;32mSuccess\x1B[0m", "Success")]
    [InlineData("\x1B[0m", "")]
    public void Strip_StandardColorCodes_RemovesThem(string input, string expected)
    {
        Assert.Equal(expected, AnsiStripper.Strip(input));
    }

    [Theory]
    [InlineData("\x1B[2J", "")]       // Clear screen
    [InlineData("\x1B[H", "")]        // Cursor home
    [InlineData("\x1B[10A", "")]      // Cursor up 10
    [InlineData("\x1B[5B", "")]       // Cursor down 5
    public void Strip_CursorMovementCodes_RemovesThem(string input, string expected)
    {
        Assert.Equal(expected, AnsiStripper.Strip(input));
    }

    [Fact]
    public void Strip_OscSequence_RemovesIt()
    {
        // OSC (Operating System Command) sequences end with BEL (\x07)
        var input = "\x1B]0;Window Title\x07Some text";
        Assert.Equal("Some text", AnsiStripper.Strip(input));
    }

    [Fact]
    public void Strip_BareBracketSequences_RemovesThem()
    {
        // Some CLI tools emit bracket sequences without ESC prefix
        var input = "[32mGreen text[0m";
        Assert.Equal("Green text", AnsiStripper.Strip(input));
    }

    [Fact]
    public void Strip_EraseLineSequence_RemovesIt()
    {
        var input = "Some text[K";
        Assert.Equal("Some text", AnsiStripper.Strip(input));
    }

    [Theory]
    [InlineData("Some text[2K", "Some text")]
    [InlineData("[2K[1GDownloading", "Downloading")]
    [InlineData("\x1B[2K[1GDownloading", "Downloading")]
    [InlineData("[0m[KDone", "Done")]
    public void Strip_BareCursorAndEraseCodesNextToOtherCodesOrAtTheEnd_RemovesThem(string input, string expected)
    {
        Assert.Equal(expected, AnsiStripper.Strip(input));
    }

    [Theory]
    [InlineData("See [Kubernetes docs](https://kubernetes.io)")]
    [InlineData("value = dict[Key]")]
    [InlineData("[1st place] and [2nd]")]
    [InlineData("items[0] and arr[10A]")]
    [InlineData("Dict[K, V] and Map[K]")]
    [InlineData("a [3D printing] model and [2D array]")]
    public void Strip_PlainTextWithBrackets_IsUnchanged(string input)
    {
        Assert.Equal(input, AnsiStripper.Strip(input));
    }

    [Fact]
    public void Strip_MixedContent_PreservesPlainText()
    {
        var input = "\x1B[1m✓\x1B[0m 10 tests passed in \x1B[32m2.5s\x1B[0m";
        Assert.Equal("✓ 10 tests passed in 2.5s", AnsiStripper.Strip(input));
    }

    [Fact]
    public void Strip_MultipleSequencesInRow_RemovesAll()
    {
        var input = "\x1B[1m\x1B[31m\x1B[4mBold Red Underline\x1B[0m";
        Assert.Equal("Bold Red Underline", AnsiStripper.Strip(input));
    }
}
