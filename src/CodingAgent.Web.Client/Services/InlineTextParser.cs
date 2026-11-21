namespace CodingAgent.Web.Services;

public enum InlineKind { Text, Code, Bold }

/// <summary>One run of agent text: plain, a code span or a bold span. <see cref="Text"/> never holds the marks.</summary>
public readonly record struct InlineToken(InlineKind Kind, string Text);

/// <summary>
/// Splits agent-written text into plain text, <c>`code`</c> and <c>**bold**</c>, nothing else. The result is
/// plain strings for the caller to render as text, so agent text can never become markup. Whatever is not
/// a complete mark stays literal text.
/// </summary>
public static class InlineTextParser
{
    private const string CodeMark = "`";
    private const string BoldMark = "**";

    /// <summary>
    /// A mark opens a span when the same mark closes it later on the same line; an empty pair
    /// (<c>****</c>) and a mark with no partner stay literal. Code is not parsed inside, and bold content
    /// is not parsed further. Text runs keep every character, so joining the tokens with their marks
    /// gives the input back.
    /// </summary>
    public static IReadOnlyList<InlineToken> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var tokens = new List<InlineToken>();
        var literalStart = 0;
        var i = 0;
        while (i < text.Length)
        {
            var mark = MarkAt(text, i);
            if (mark is null)
            {
                i++;
                continue;
            }

            var contentStart = i + mark.Length;
            var close = FindClose(text, contentStart, mark);
            if (close < 0)
            {
                i = contentStart;
                continue;
            }

            if (close == contentStart)
            {
                i = close + mark.Length;
                continue;
            }

            if (i > literalStart)
            {
                tokens.Add(new InlineToken(InlineKind.Text, text[literalStart..i]));
            }

            tokens.Add(new InlineToken(mark == CodeMark ? InlineKind.Code : InlineKind.Bold, text[contentStart..close]));
            i = literalStart = close + mark.Length;
        }

        if (literalStart < text.Length)
        {
            tokens.Add(new InlineToken(InlineKind.Text, text[literalStart..]));
        }

        return tokens;
    }

    private static string? MarkAt(string text, int index)
    {
        if (text[index] == '`')
        {
            return CodeMark;
        }

        return text[index] == '*' && index + 1 < text.Length && text[index + 1] == '*' ? BoldMark : null;
    }

    /// <summary>The index of the next <paramref name="mark"/> from <paramref name="start"/> on the same line, or -1.</summary>
    private static int FindClose(string text, int start, string mark)
    {
        var rest = text.AsSpan(start);
        var lineLength = rest.IndexOfAny('\n', '\r');
        if (lineLength >= 0)
        {
            rest = rest[..lineLength];
        }

        var found = rest.IndexOf(mark.AsSpan(), StringComparison.Ordinal);
        return found < 0 ? -1 : start + found;
    }
}
