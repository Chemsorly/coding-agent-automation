namespace CodingAgentWebUI.Pipeline.Models;

public sealed class QualityGateReport
{
    public required GateResult Compilation { get; init; }
    public required GateResult Tests { get; init; }
    public GateResult? Coverage { get; init; }
    public GateResult? SecurityScan { get; init; }
    public GateResult? ExternalCi { get; init; }
    public bool AllPassed => Compilation.Passed && Tests.Passed
        && (Coverage?.Passed ?? true) && (SecurityScan?.Passed ?? true)
        && (ExternalCi?.Passed ?? true);
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
