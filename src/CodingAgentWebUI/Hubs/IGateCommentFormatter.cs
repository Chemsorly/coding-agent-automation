namespace CodingAgentWebUI.Hubs;

/// <summary>
/// Formats gate comments (not-ready or wont-do) from assessment JSON.
/// Extracted from AgentHub.Pipeline.cs to keep the hub thin.
/// </summary>
public interface IGateCommentFormatter
{
    /// <summary>
    /// Builds a formatted gate comment from the assessment JSON payload.
    /// Falls back to raw JSON wrapped in a code block if deserialization fails.
    /// </summary>
    /// <param name="assessmentJson">The assessment JSON to deserialize, or null for a header-only comment.</param>
    /// <param name="isWontDo">True for "Won't Do" gate, false for "Needs Refinement" gate.</param>
    string FormatGateComment(string? assessmentJson, bool isWontDo);
}
