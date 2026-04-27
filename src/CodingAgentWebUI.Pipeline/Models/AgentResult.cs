namespace CodingAgentWebUI.Pipeline.Models;

public sealed class AgentResult
{
    public required int ExitCode { get; init; }
    public required IReadOnlyList<string> OutputLines { get; init; }
    public bool Success => ExitCode == 0;
}
