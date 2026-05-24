namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Which side of the diff an inline comment applies to.
/// For this implementation, all findings target Right (new file state) because
/// the FindingsParser extracts line numbers from the new file.
/// Left is reserved for future use (detecting deleted-line references).
/// </summary>
public enum DiffSide
{
    /// <summary>Old file state (deleted lines). Reserved for future use.</summary>
    Left,

    /// <summary>New file state (added or modified lines). Default for all findings.</summary>
    Right
}
