namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Thrown when the analysis phase produces incomplete or missing artifacts
/// (analysis.md missing/too short, analysis-assessment.json missing/malformed).
/// Caught by the analysis retry loop for retryable failures.
/// </summary>
// TODO: [RES-06] Add parameterless constructor per CA1032 (review finding .NET #2)
public sealed class AnalysisIncompleteException : Exception
{
    public AnalysisIncompleteException(string message) : base(message) { }

    public AnalysisIncompleteException(string message, Exception? innerException)
        : base(message, innerException) { }
}
