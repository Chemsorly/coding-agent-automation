namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A parsed code review finding with file/line metadata extracted from agent output.
/// Immutable record with required init-only properties.
/// </summary>
/// <remarks>
/// Invariant: When <see cref="FilePath"/> is null, <see cref="LineNumber"/> must be 0.
/// When <see cref="FilePath"/> is non-null, <see cref="LineNumber"/> must be >= 1.
/// <see cref="FilePath"/> must use forward-slash separators only (no backslashes).
/// These invariants are enforced via property validation.
/// </remarks>
public sealed record StructuredFinding
{
    private string? _filePath;
    private int _lineNumber;
    private string _message = string.Empty;

    /// <summary>Severity level of the finding.</summary>
    public required FindingSeverity Severity { get; init; }

    /// <summary>
    /// Relative file path (forward slashes, no backslashes) or null for findings without location.
    /// </summary>
    public string? FilePath
    {
        get => _filePath;
        init
        {
            if (value is not null && value.Contains('\\'))
                throw new ArgumentException("FilePath must use forward slashes only.", nameof(FilePath));
            _filePath = value;
        }
    }

    /// <summary>
    /// 1-based line number in the new file. 0 when FilePath is null (no location).
    /// Must be >= 1 when FilePath is non-null.
    /// </summary>
    public int LineNumber
    {
        get => _lineNumber;
        init
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(LineNumber), "LineNumber must not be negative.");
            _lineNumber = value;
        }
    }

    /// <summary>Finding description text (max 65536 chars).</summary>
    public required string Message
    {
        get => _message;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length > 65536)
                throw new ArgumentException("Message must not exceed 65536 characters.", nameof(Message));
            _message = value;
        }
    }

    private string _agentName = string.Empty;

    /// <summary>Name of the review agent that produced this finding.</summary>
    public required string AgentName
    {
        get => _agentName;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _agentName = value;
        }
    }
}
