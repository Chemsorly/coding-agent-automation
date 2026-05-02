namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Thrown when the analysis phase produces incomplete or missing artifacts
/// (analysis.md missing/too short, analysis-assessment.json missing/malformed).
/// Caught by the analysis retry loop for retryable failures.
/// </summary>
public sealed class AnalysisIncompleteException : Exception
{
    public AnalysisIncompleteException() : base("Analysis produced incomplete artifacts.") { }

    public AnalysisIncompleteException(string message) : base(message) { }

    public AnalysisIncompleteException(string message, Exception? innerException)
        : base(message, innerException) { }
}
