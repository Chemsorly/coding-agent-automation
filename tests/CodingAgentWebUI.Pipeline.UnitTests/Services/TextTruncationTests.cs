using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class TextTruncationTests
{
    [Fact]
    public void TruncateAtSentenceBoundary_TextUnderLimit_ReturnsUnchanged()
    {
        var text = "This is a short sentence.";

        var result = TextTruncation.TruncateAtSentenceBoundary(text, 500);

        result.Should().Be(text);
    }

    [Fact]
    public void TruncateAtSentenceBoundary_TextExactlyAtLimit_ReturnsUnchanged()
    {
        var text = new string('a', 500);

        var result = TextTruncation.TruncateAtSentenceBoundary(text, 500);

        result.Should().Be(text);
    }

    [Fact]
    public void TruncateAtSentenceBoundary_TextOverLimit_WithSentenceBoundary_TruncatesAtBoundary()
    {
        // Build a text that's over 500 chars with a ". " before the 500 mark
        var firstSentence = new string('a', 200) + ". ";
        var secondSentence = new string('b', 400);
        var text = firstSentence + secondSentence;

        var result = TextTruncation.TruncateAtSentenceBoundary(text, 500);

        // Should truncate at the period after the first sentence
        result.Should().EndWith("...");
        result.Should().Contain(new string('a', 200) + ".");
        result.Length.Should().BeLessThan(text.Length);
    }

    [Fact]
    public void TruncateAtSentenceBoundary_TextOverLimit_NoPeriodSpace_HardTruncates()
    {
        var text = new string('x', 600); // No ". " anywhere

        var result = TextTruncation.TruncateAtSentenceBoundary(text, 500);

        result.Should().Be(new string('x', 500) + "...");
    }

    [Fact]
    public void TruncateAtSentenceBoundary_PeriodSpaceTooEarly_HardTruncates()
    {
        // Period-space at position 10 (less than 20% of 500 = 100), so it should hard-truncate
        var text = "Short. " + new string('a', 600);

        var result = TextTruncation.TruncateAtSentenceBoundary(text, 500);

        result.Should().HaveLength(503); // 500 + "..."
        result.Should().EndWith("...");
    }

    [Fact]
    public void TruncateAtSentenceBoundary_EmptyString_ReturnsEmpty()
    {
        var result = TextTruncation.TruncateAtSentenceBoundary("", 500);

        result.Should().Be("");
    }

    [Fact]
    public void TruncateAtSentenceBoundary_NullString_ReturnsNull()
    {
        var result = TextTruncation.TruncateAtSentenceBoundary(null!, 500);

        result.Should().BeNull();
    }

    [Fact]
    public void TruncateAtSentenceBoundary_MultipleSentenceBoundaries_TruncatesAtLast()
    {
        // Multiple ". " within the 500-char window — should use the last one
        var text = new string('a', 150) + ". " + new string('b', 150) + ". " + new string('c', 300);

        var result = TextTruncation.TruncateAtSentenceBoundary(text, 500);

        // Should truncate at the second ". " (position ~304)
        result.Should().EndWith("...");
        result.Should().Contain(new string('b', 150) + ".");
    }
}
