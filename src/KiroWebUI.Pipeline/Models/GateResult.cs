namespace KiroWebUI.Pipeline.Models;

public sealed class GateResult
{
    public required string GateName { get; init; }
    public required bool Passed { get; init; }
    public string? Details { get; init; }
    public int? TestsPassed { get; init; }
    public int? TestsFailed { get; init; }
    public int? TestsSkipped { get; init; }
    public double? CoveragePercent { get; init; }
}
