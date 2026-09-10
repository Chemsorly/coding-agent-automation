using MessagePack;

namespace CodingAgent.Pipeline.Models;

[MessagePackObject]
public sealed class QualityGateReport
{
    [Key(0)]
    public required GateResult Compilation { get; init; }

    // Key(1) is retired (was Coverage). Do not reuse to avoid deserialization issues with existing data.

    [Key(2)]
    public GateResult? ExternalCi { get; init; }

    /// <summary>Per-QGC detailed results (populated in multi-QGC mode).</summary>
    [Key(3)]
    public IReadOnlyList<QgcExecutionResult> QgcResults { get; init; } = [];

    // Key(4) is retired (was SecurityScan). Do not reuse to avoid deserialization issues with existing data.

    [Key(5)]
    public required GateResult Tests { get; init; }

    [Key(6)]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [IgnoreMember]
    public bool AllPassed => QgcResults.Count > 0
        ? QgcResults.All(r => r.Passed) && (ExternalCi?.Passed ?? true)
        : Compilation.Passed && Tests.Passed
            && (ExternalCi?.Passed ?? true);
}
