using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Property-based tests verifying that the OpenCode StripAnsiEscapes wrapper delegates
/// to KiroCliLib.Core.AnsiStripper and produces identical output for all inputs.
///
/// Feature: 023-agent-project-split, Property 1: The OpenCode StripAnsiEscapes wrapper
/// produces the same output as calling KiroCliLib.Core.AnsiStripper.Strip directly.
///
/// **Validates: Requirements 2.4**
/// </summary>
[Trait("Feature", "023-agent-project-split")]
[Trait("Property", "1")]
public class AnsiStripperEquivalenceTests
{
    /// <summary>
    /// Property 1: For any string containing a mix of plain text, ANSI CSI sequences,
    /// OSC sequences, and bare bracket sequences, the OpenCode StripAnsiEscapes wrapper
    /// produces the same output as KiroCliLib.Core.AnsiStripper.Strip.
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = [typeof(AnsiMixedStringArbitrary)])]
    public void StripAnsiEscapes_DelegatesToKiroCliLibAnsiStripper(AnsiMixedString input)
    {
        // Act
        var wrapperResult = CodingAgentWebUI.Agent.OpenCode.OpenCodeAgentProvider.StripAnsiEscapes(input.Value);
        var directResult = KiroCliLib.Core.AnsiStripper.Strip(input.Value);

        // Assert — wrapper produces identical output to direct call
        Assert.Equal(directResult, wrapperResult);
    }
}

/// <summary>
/// Wrapper for strings that contain a mix of plain text and ANSI escape sequences.
/// </summary>
public sealed class AnsiMixedString
{
    public string Value { get; }
    public AnsiMixedString(string value) => Value = value;
    public override string ToString() => $"\"{Value}\"";
}

/// <summary>
/// FsCheck arbitrary that generates random strings including plain text, ANSI CSI sequences,
/// OSC sequences, and bare bracket sequences to thoroughly test equivalence.
/// </summary>
public static class AnsiMixedStringArbitrary
{
    /// <summary>
    /// ANSI CSI sequences (ESC [ ... letter).
    /// </summary>
    private static readonly string[] CsiSequences =
    [
        "\x1b[0m",
        "\x1b[1m",
        "\x1b[31m",
        "\x1b[32m",
        "\x1b[1;32m",
        "\x1b[38;5;196m",
        "\x1b[48;2;0;128;0m",
        "\x1b[H",
        "\x1b[10A",
        "\x1b[5B",
        "\x1b[3C",
        "\x1b[2D",
        "\x1b[K",
        "\x1b[2J",
    ];

    /// <summary>
    /// OSC sequences (ESC ] ... BEL).
    /// </summary>
    private static readonly string[] OscSequences =
    [
        "\x1b]0;Window Title\x07",
        "\x1b]2;Tab Title\x07",
        "\x1b]8;;https://example.com\x07",
        "\x1b]0;some-process running\x07",
    ];

    /// <summary>
    /// Bare bracket sequences (no ESC prefix, emitted by some CLI tools).
    /// </summary>
    private static readonly string[] BareBracketSequences =
    [
        "[32m",
        "[0m",
        "[1;33m",
        "[K",
        "[31m",
        "[38;5;200m",
    ];

    /// <summary>
    /// Characters safe for plain text segments.
    /// </summary>
    private const string PlainChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 !@#$%^&*()_+-={}|:\"<>?,./~`';\t\n";

    public static Arbitrary<AnsiMixedString> AnsiMixedStrings()
    {
        // Generator for plain text segments
        var plainSegmentGen =
            from len in Gen.Choose(0, 20)
            from chars in Gen.ArrayOf(Gen.Elements(PlainChars.ToCharArray()), len)
            select new string(chars);

        // Generator for any ANSI sequence type
        var allSequences = CsiSequences.Concat(OscSequences).Concat(BareBracketSequences).ToArray();
        var ansiGen = Gen.Elements(allSequences);

        // Generator for a single fragment: either plain text or an ANSI sequence
        var fragmentGen = Gen.OneOf(
            plainSegmentGen,
            ansiGen
        );

        // Build a string from 1-8 fragments concatenated together
        var gen =
            from count in Gen.Choose(1, 8)
            from fragments in Gen.ArrayOf(fragmentGen, count)
            select new AnsiMixedString(string.Concat(fragments));

        return gen.ToArbitrary();
    }
}
