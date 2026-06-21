using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

[MessagePackObject]
public sealed class GateResult
{
    [Key(0)]
    public double? CoveragePercent { get; init; }

    [Key(1)]
    public string? Details { get; init; }

    [Key(2)]
    public required string GateName { get; init; }

    [Key(3)]
    public required bool Passed { get; init; }

    [Key(4)]
    public IReadOnlyList<string>? QuarantinedTestNames { get; init; }

    [Key(5)]
    public int? TestsFailed { get; init; }

    [Key(6)]
    public int? TestsPassed { get; init; }

    [Key(7)]
    public int? TestsQuarantined { get; init; }

    [Key(8)]
    public int? TestsSkipped { get; init; }
}
