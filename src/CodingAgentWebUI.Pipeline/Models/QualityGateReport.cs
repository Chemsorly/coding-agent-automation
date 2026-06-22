using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

[MessagePackObject]
public sealed class QualityGateReport
{
    [Key(0)]
    public required GateResult Compilation { get; init; }

    [Key(1)]
    public GateResult? Coverage { get; init; }

    [Key(2)]
    public GateResult? ExternalCi { get; init; }

    /// <summary>Per-QGC detailed results (populated in multi-QGC mode).</summary>
    [Key(3)]
    public IReadOnlyList<QgcExecutionResult> QgcResults { get; init; } = [];

    [Key(4)]
    public GateResult? SecurityScan { get; init; }

    [Key(5)]
    public required GateResult Tests { get; init; }

    [Key(6)]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [IgnoreMember]
    public bool AllPassed => QgcResults.Count > 0
        ? QgcResults.All(r => r.Passed) && (ExternalCi?.Passed ?? true)
        : Compilation.Passed && Tests.Passed
            && (Coverage?.Passed ?? true) && (SecurityScan?.Passed ?? true)
            && (ExternalCi?.Passed ?? true);
}
