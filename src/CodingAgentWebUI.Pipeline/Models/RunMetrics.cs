using System.Collections.Concurrent;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Cohesive sub-state group for execution metrics accumulated during a pipeline run.
/// Part of the PipelineRun decomposition (MAINT-13).
/// </summary>
public sealed class RunMetrics
{
    /// <summary>Number of quality-gate retry attempts consumed.</summary>
    public int RetryCount { get; set; }

    /// <summary>Number of infrastructure CI retries (does not consume agent retry budget).</summary>
    public int InfrastructureRetryCount { get; set; }

    /// <summary>Number of files changed during code generation, updated after agent execution.</summary>
    public int FilesChangedCount { get; set; }

    /// <summary>Lines added during code generation.</summary>
    public int LinesAdded { get; set; }

    /// <summary>Lines removed during code generation.</summary>
    public int LinesRemoved { get; set; }

    /// <summary>Accumulated total tokens across all agent invocations in this run.</summary>
    public long TotalTokens { get; set; }

    /// <summary>Accumulated total cost (USD, decimal) across all agent invocations, or null if no cost data available.</summary>
    public decimal? TotalCost { get; set; }

    /// <summary>Per-phase token/cost breakdown accumulated during the run. Thread-safe for concurrent review agents.</summary>
    public ConcurrentDictionary<string, PhaseUsage> PhaseBreakdown { get; } = new();
}
