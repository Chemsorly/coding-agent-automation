using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Agent.OpenCode;
using KiroCliLib.Core;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Property-based tests for ANSI escape sequence stripping in OpenCodeAgentProvider.
/// Feature: opencode-agent-executor, Property 6: ANSI Escape Sequence Stripping
///
/// For any output string containing ANSI escape sequences (CSI codes, OSC sequences, color codes),
/// all escape sequences SHALL be removed AND all non-escape content SHALL be preserved unchanged
/// before the string is included in AgentResult.OutputLines or forwarded to the onOutputLine callback.
///
/// **Validates: Requirements 4.9, 11.7**
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "6")]
public class OpenCodeAnsiStrippingPropertyTests
{
    /// <summary>
    /// Property 6a: For any clean text with ANSI sequences inserted at random positions,
    /// stripping the sequences yields the original clean text unchanged.
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(AnsiInjectedStringArbitrary)])]
    public void StripAnsiEscapes_RemovesAllSequences_PreservesCleanContent(AnsiInjectedString input)
    {
        // Act — strip ANSI sequences using the OpenCodeAgentProvider utility
        var result = OpenCodeAgentProvider.StripAnsiEscapes(input.Dirty);

        // Assert — the result equals the original clean text (all ANSI removed, content preserved)
        Assert.Equal(input.Clean, result);
    }

    /// <summary>
    /// Property 6b: Stripping is idempotent — applying Strip twice yields the same result as once.
    /// This proves no partial escape sequences remain after the first strip.
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(AnsiInjectedStringArbitrary)])]
    public void StripAnsiEscapes_IsIdempotent(AnsiInjectedString input)
    {
        // Act
        var firstStrip = OpenCodeAgentProvider.StripAnsiEscapes(input.Dirty);
        var secondStrip = OpenCodeAgentProvider.StripAnsiEscapes(firstStrip);

        // Assert — second strip produces the same result as first
        Assert.Equal(firstStrip, secondStrip);
    }

    /// <summary>
    /// Property 6c: Clean strings (no ANSI sequences) pass through unchanged.
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(CleanStringArbitrary)])]
    public void StripAnsiEscapes_CleanString_PassesThroughUnchanged(CleanString input)
    {
        // Act
        var result = OpenCodeAgentProvider.StripAnsiEscapes(input.Value);

        // Assert — clean text is preserved exactly
        Assert.Equal(input.Value, result);
    }

    /// <summary>
    /// Property 6d: Null and empty inputs return empty string.
    /// </summary>
    [Fact]
    public void StripAnsiEscapes_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, OpenCodeAgentProvider.StripAnsiEscapes(null));
        Assert.Equal(string.Empty, OpenCodeAgentProvider.StripAnsiEscapes(string.Empty));
    }
}

/// <summary>
/// Represents a string with known clean content and a dirty version with ANSI sequences injected.
/// </summary>
public sealed class AnsiInjectedString
{
    /// <summary>The original text without any ANSI sequences.</summary>
    public string Clean { get; }

    /// <summary>The text with ANSI escape sequences inserted at random positions.</summary>
    public string Dirty { get; }

    public AnsiInjectedString(string clean, string dirty)
    {
        Clean = clean;
        Dirty = dirty;
    }

    public override string ToString() => $"Clean=\"{Clean}\", Dirty=\"{Dirty}\"";
}

/// <summary>
/// FsCheck arbitrary that generates strings with known clean content and ANSI sequences
/// injected at random positions. This allows verifying that stripping recovers the original.
/// </summary>
public static class AnsiInjectedStringArbitrary
{
    /// <summary>
    /// ANSI escape sequences covering CSI codes, OSC sequences, and color codes.
    /// These match the patterns handled by AnsiStripper's regex.
    /// </summary>
    private static readonly string[] AnsiSequences =
    [
        // CSI color codes
        "\x1b[0m",              // Reset
        "\x1b[1m",              // Bold
        "\x1b[31m",             // Red foreground
        "\x1b[32m",             // Green foreground
        "\x1b[1;32m",           // Bold green
        "\x1b[38;5;196m",       // 256-color red
        "\x1b[48;2;0;128;0m",   // 24-bit green background
        // CSI cursor movement
        "\x1b[H",               // Cursor home
        "\x1b[10A",             // Cursor up 10
        "\x1b[5B",              // Cursor down 5
        "\x1b[3C",              // Cursor forward 3
        "\x1b[2D",              // Cursor back 2
        // CSI erase
        "\x1b[K",               // Erase to end of line
        "\x1b[2J",              // Clear screen
        "\x1b[1J",              // Clear screen (above cursor)
        // OSC sequences (terminated by BEL \x07)
        "\x1b]0;Window Title\x07",
        "\x1b]2;Tab Title\x07",
        "\x1b]8;;https://example.com\x07",
        // Bare bracket sequences (some CLI tools emit without ESC prefix)
        "[32m",
        "[0m",
        "[1;33m",
        "[K",
    ];

    /// <summary>
    /// Characters that are safe for "clean" text — they don't form part of ANSI sequences.
    /// Excludes ESC (\x1b), BEL (\x07), and bracket patterns that could be matched by the regex.
    /// </summary>
    private const string SafeChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 !@#$%^&*()_+-={}|:\"<>?,./~`';\t";

    public static Arbitrary<AnsiInjectedString> AnsiInjectedStrings()
    {
        var charGen = Gen.Elements(SafeChars.ToCharArray());

        // Generate clean text segments (1-15 chars each)
        var segmentGen =
            from len in Gen.Choose(1, 15)
            from chars in Gen.ArrayOf(charGen, len)
            select new string(chars);

        // Generate an ANSI sequence
        var ansiGen =
            from idx in Gen.Choose(0, AnsiSequences.Length - 1)
            select AnsiSequences[idx];

        // Generate a single pair of (clean text, ansi sequence)
        var pairGen =
            from clean in segmentGen
            from ansi in ansiGen
            select new CleanAnsiPair(clean, ansi);

        // Build a string with 1-5 clean segments, each followed by an ANSI sequence,
        // plus a final clean suffix. This guarantees at least one ANSI sequence.
        var gen =
            from segmentCount in Gen.Choose(1, 5)
            from pairs in Gen.ArrayOf(pairGen, segmentCount)
            from suffix in segmentGen
            let cleanText = string.Concat(pairs.Select(p => p.Clean)) + suffix
            let dirtyText = string.Concat(pairs.Select(p => p.Clean + p.Ansi)) + suffix
            select new AnsiInjectedString(cleanText, dirtyText);

        return gen.ToArbitrary();
    }
}

/// <summary>
/// Helper record to hold a pair of clean text and ANSI sequence for generator composition.
/// </summary>
internal sealed record CleanAnsiPair(string Clean, string Ansi);

/// <summary>
/// Wrapper for strings guaranteed to contain no ANSI escape sequences.
/// </summary>
public sealed class CleanString
{
    public string Value { get; }
    public CleanString(string value) => Value = value;
    public override string ToString() => Value;
}

/// <summary>
/// FsCheck arbitrary that generates strings with no ANSI escape sequences.
/// </summary>
public static class CleanStringArbitrary
{
    /// <summary>
    /// Characters that cannot form ANSI sequences — excludes ESC byte and
    /// avoids patterns that look like bare bracket sequences.
    /// </summary>
    private const string SafeChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 !@#$%^&*()_+-={}|:\"<>?,./~`';\t\n";

    public static Arbitrary<CleanString> CleanStrings()
    {
        var charGen = Gen.Elements(SafeChars.ToCharArray());

        var gen =
            from len in Gen.Choose(0, 50)
            from chars in Gen.ArrayOf(charGen, len)
            select new CleanString(new string(chars));

        return gen.ToArbitrary();
    }
}
