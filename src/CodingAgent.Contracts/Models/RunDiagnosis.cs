namespace CodingAgent.Pipeline.Models;

/// <summary>Who has to act on a failed run.</summary>
public enum RunDiagnosisVerdict
{
    /// <summary>A platform hiccup; re-dispatching is enough.</summary>
    Transient,
    /// <summary>A setting of this deployment must change (memory, timeout, credentials).</summary>
    Configuration,
    /// <summary>The repository is the cause (a gate that fails across runs).</summary>
    Repository,
    /// <summary>The agent crashed or could not finish the change.</summary>
    Agent,
    /// <summary>No rule matched.</summary>
    Unknown
}

/// <summary>The rule-based diagnosis of a failed run, see <c>RunDiagnoser</c>.</summary>
public sealed record RunDiagnosis(
    RunDiagnosisVerdict Verdict,
    string Title,
    string Explanation,
    string NextStep,
    IReadOnlyList<string> Evidence);
