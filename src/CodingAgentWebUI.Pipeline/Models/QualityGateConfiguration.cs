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

    [Key(7)]
    public double? CoverageThreshold { get; init; }

    // Key(8) is retired (was SecurityScanEnabled). Do not reuse to avoid deserialization issues with existing data.

    [Key(9)]
    public bool Enabled { get; init; } = true;

    [Key(10)]
    public int ExecutionOrder { get; init; } = 0;

    /// <summary>
    /// Format of the coverage report produced by the test command.
    /// Supported values: "cobertura" (default), "jacoco".
    /// </summary>
    [Key(11)]
    public string CoverageReportFormat { get; init; } = "cobertura";

    /// <summary>
    /// Glob patterns for locating coverage report files relative to the workspace root.
    /// If null/empty, defaults are used based on the test command:
    /// - dotnet: TestResults/**/coverage.cobertura.xml
    /// - jacoco: **/target/site/jacoco/jacoco.xml
    /// - cobertura (non-dotnet): **/coverage.xml
    /// </summary>
    [Key(12)]
    public IReadOnlyList<string>? CoverageReportPaths { get; init; }
}
