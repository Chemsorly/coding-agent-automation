using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Removes text a person does not see on the rendered page from text written by someone outside the repository:
/// HTML comments, Markdown reference definitions, Unicode format characters, the Tags block and variation selectors.
/// Applied to unverified text only, where it goes into a prompt or a context file. It is not a claim that a reader
/// sees all an agent sees: link titles, image alt text, fence info strings and closed details blocks remain.
/// </summary>
public static partial class AgentTextNormalizer
{
    private const string CommentOpen = "<!--";
    private const string CommentClose = "-->";

    /// <summary>Returns <paramref name="text"/> without its hidden parts. Null gives an empty string.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Invisible characters go first, so that one placed inside a marker such as "<!--" cannot hide the marker.
        return new HiddenMarkdownScanner(RemoveInvisibleCharacters(text)).Run();
    }

    private static string RemoveInvisibleCharacters(string text)
    {
        var output = new StringBuilder(text.Length);
        Span<char> utf16 = stackalloc char[2];

        // By Rune, not by char: the Tags block and the supplementary variation selectors are surrogate pairs, and
        // char.GetUnicodeCategory reports each half as Surrogate. EnumerateRunes turns a lone surrogate into U+FFFD.
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsInvisible(rune))
                output.Append(utf16[..rune.EncodeToUtf16(utf16)]);
        }

        return output.ToString();
    }

    private static bool IsInvisible(Rune rune) =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
        || rune.Value is >= 0xE0000 and <= 0xE007F   // Tags block, including its unassigned code points
        || rune.Value is >= 0xFE00 and <= 0xFE0F     // Variation selectors (category Mn, not Cf)
        || rune.Value is >= 0xE0100 and <= 0xE01EF   // Variation selectors supplement (category Mn)
        || rune.Value is >= 0x180B and <= 0x180F;    // Mongolian variation selectors (category Mn)

    // [label]: destination "optional title", also with the destination or the title on the next line.
    // "[^" is a footnote, which the page shows. "[Bug]: crash on start" has no valid title, so it is no definition.
    [GeneratedRegex(
        """\G {0,3}\[(?!\^)(?:[^\[\]\\]|\\.){1,999}\]:[ \t]*(?:\r?\n[ \t]*)?(?:<[^<>\r\n]*>|[^\s<]\S*)(?:(?:[ \t]+|[ \t]*\r?\n[ \t]*)(?:"[^"]*"|'[^']*'|\([^()]*\)))?[ \t]*(?:\r?\n|\z)""",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReferenceDefinition();

    /// <summary>
    /// Copies the text line by line without its HTML comments and reference definitions. A comment inside a fenced
    /// code block is visible on the page, so it stays. Uses IndexOf rather than one regular expression over the whole
    /// text, so that many unterminated comment openers stay linear.
    /// </summary>
    private sealed class HiddenMarkdownScanner(string text)
    {
        private const int NewlineUnknown = -2;

        private readonly StringBuilder _output = new(text.Length);
        private int _position;
        private int _newline = NewlineUnknown;
        private bool _atLineStart = true;
        private bool _noCloserLeft;
        private char _fenceChar;
        private int _fenceLength;

        public string Run()
        {
            while (_position < text.Length)
            {
                // _position only moves forward, so a newline found once stays the next one until _position passes it,
                // and "no newline left" stays true. Without this, a long single line rescans its remainder per comment.
                if (_newline == NewlineUnknown || (_newline >= 0 && _newline < _position))
                    _newline = text.IndexOf('\n', _position);

                var lineEnd = _newline < 0 ? text.Length : _newline + 1;
                var line = text.AsSpan(_position, lineEnd - _position);

                if (TryCopyFenceLine(line, lineEnd) || TrySkipReferenceDefinition() || TryHandleComment(line))
                    continue;

                _output.Append(line);
                _position = lineEnd;
                _atLineStart = true;
            }

            return _output.ToString();
        }

        private bool TryCopyFenceLine(ReadOnlySpan<char> line, int lineEnd)
        {
            if (_fenceLength > 0)
            {
                if (IsClosingFence(line, _fenceChar, _fenceLength))
                    _fenceLength = 0;
            }
            else if (!_atLineStart || !TryOpenFence(line, out _fenceChar, out _fenceLength))
            {
                return false;
            }

            _output.Append(line);
            _position = lineEnd;
            return true;
        }

        private bool TrySkipReferenceDefinition()
        {
            if (!_atLineStart)
                return false;

            var definition = ReferenceDefinition().Match(text, _position);
            if (!definition.Success)
                return false;

            _position += definition.Length;
            return true;
        }

        private bool TryHandleComment(ReadOnlySpan<char> line)
        {
            var open = line.IndexOf(CommentOpen, StringComparison.Ordinal);
            if (open < 0)
                return false;

            _output.Append(line[..open]);

            // Search from the opener's own dashes: "<!-->" and "<!--->" are complete, empty comments.
            var close = _noCloserLeft ? -1 : text.IndexOf(CommentClose, _position + open + 2, StringComparison.Ordinal);
            if (close >= 0)
            {
                _position = close + CommentClose.Length;
            }
            else if (_atLineStart && open <= 3 && line[..open].IsWhiteSpace())
            {
                // Unterminated at the start of a line: an HTML block that hides everything to the end of the text.
                _position = text.Length;
            }
            else
            {
                // Unterminated in the middle of a line: visible text, for example a report about the opener itself.
                _noCloserLeft = true;
                _output.Append(CommentOpen);
                _position += open + CommentOpen.Length;
            }

            _atLineStart = false;
            return true;
        }

        // NOTE: Fence tracking ignores HTML blocks, so a hidden comment can survive: in "<div>\n```\n<!-- payload -->" with no
        // blank line, the page shows the backticks as text and hides the comment, but this scanner takes them as an open fence and
        // copies the payload verbatim into the prompt. A fix needs HTML block tracking, a design change; the doc comment on
        // AgentTextNormalizer already says this is not a guarantee.
        /// <summary>
        /// A fence counts only at column 0. An indented one may belong to a list item, whose end also ends the fence
        /// without a closing line, and treating it as still open would keep a comment the page hides.
        /// </summary>
        private static bool TryOpenFence(ReadOnlySpan<char> line, out char fenceChar, out int fenceLength)
        {
            fenceChar = '\0';
            fenceLength = 0;
            if (line.IsEmpty || line[0] is not ('`' or '~'))
                return false;

            var length = line.IndexOfAnyExcept(line[0]);
            if (length < 0)
                length = line.Length;
            if (length < 3)
                return false;

            // A backtick fence's info string cannot contain a backtick: such a line is a paragraph with inline code.
            if (line[0] == '`' && line[length..].Contains('`'))
                return false;

            fenceChar = line[0];
            fenceLength = length;
            return true;
        }

        private static bool IsClosingFence(ReadOnlySpan<char> line, char fenceChar, int fenceLength)
        {
            var trimmed = line.TrimEnd();
            var indent = trimmed.IndexOfAnyExcept(' ');
            if (indent is < 0 or > 3)
                return false;

            var fence = trimmed[indent..];
            return fence.Length >= fenceLength && fence.IndexOfAnyExcept(fenceChar) < 0;
        }
    }
}
