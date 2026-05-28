namespace KiroCliLib.Configuration;

/// <summary>
/// Centralized constants for KiroCliLib.
/// </summary>
public static class KiroCliConstants
{
    /// <summary>
    /// Default agent execution timeout. Coupled to PipelineConstants.DefaultAgentTimeout
    /// in CodingAgentWebUI.Pipeline — if one changes, the other must be updated.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);
}
