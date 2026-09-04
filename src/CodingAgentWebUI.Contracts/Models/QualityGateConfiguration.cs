using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A named entity that defines structured quality gate commands (compilation executable + arguments,
/// test executable + arguments, coverage threshold) keyed by a set of MatchLabels.
/// Applied to jobs whose required labels intersect with the QGC's match labels.
/// </summary>
[MessagePackObject]
public sealed record QualityGateConfiguration
{
    [Key(0)]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    [Key(1)]
    public required string DisplayName { get; init; }

    [Key(2)]
    public IReadOnlyList<string> MatchLabels { get; init; } = [];

    [Key(3)]
    public string? CompilationCommand { get; init; }

    [Key(4)]
    public IReadOnlyList<string>? CompilationArguments { get; init; }

    [Key(5)]
    public string? TestCommand { get; init; }

    [Key(6)]
    public IReadOnlyList<string>? TestArguments { get; init; }

    // Key(7) is retired (was CoverageThreshold). Do not reuse to avoid deserialization issues with existing data.

    // Key(8) is retired (was SecurityScanEnabled). Do not reuse to avoid deserialization issues with existing data.

    [Key(9)]
    public bool Enabled { get; init; } = true;

    [Key(10)]
    public int ExecutionOrder { get; init; } = 0;

    // Key(11) is retired (was CoverageReportFormat). Do not reuse to avoid deserialization issues with existing data.

    // Key(12) is retired (was CoverageReportPaths). Do not reuse to avoid deserialization issues with existing data.

    // Key(13) is retired (was TestQuarantine). Do not reuse to avoid deserialization issues with existing data.

    /// <summary>
    /// Maximum execution time in seconds for quality gate processes (compilation, tests).
    /// Processes exceeding this timeout are killed (entire process tree) and the gate is reported as failed.
    /// Default: 600 seconds (10 minutes).
    /// </summary>
    [Key(14)]
    public int ProcessTimeoutSeconds { get; init; } = 600;
}
