namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A single inline review comment to be submitted as part of a pull request review.
/// Positioned at a specific file path and line in the diff.
/// </summary>
/// <remarks>
/// Immutable sealed record with required init-only properties.
/// <see cref="Path"/> must be non-empty and use forward-slash separators only.
/// <see cref="Line"/> must be >= 1.
/// <see cref="Body"/> must not exceed 65536 characters.
/// </remarks>
public sealed record ReviewComment
{
    private string _path = string.Empty;
    private int _line;
    private string _body = string.Empty;

    /// <summary>Relative file path using forward slashes (non-empty).</summary>
    public required string Path
    {
        get => _path;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Path must not be empty.", nameof(Path));
            if (value.Contains('\\'))
                throw new ArgumentException("Path must use forward slashes only.", nameof(Path));
            _path = value;
        }
    }

    /// <summary>Line number in the diff where the comment applies (minimum 1).</summary>
    public required int Line
    {
        get => _line;
        init
        {
            if (value < 1)
                throw new ArgumentOutOfRangeException(nameof(Line), "Line must be >= 1.");
            _line = value;
        }
    }

    /// <summary>Which side of the diff (Left = old state, Right = new state). Defaults to Right.</summary>
    public DiffSide Side { get; init; } = DiffSide.Right;

    /// <summary>Comment text in Markdown (max 65536 chars).</summary>
    public required string Body
    {
        get => _body;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length > 65536)
                throw new ArgumentException("Body must not exceed 65536 characters.", nameof(Body));
            _body = value;
        }
    }
}
