namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Marks a property on <see cref="PipelineConfiguration"/> as overridable by
/// <see cref="PipelineProject"/>. Used by the reflection-based override engine in
/// <see cref="PipelineConfiguration.ApplyProjectOverrides"/> to iterate overridable
/// properties without manual if-not-null boilerplate.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class ProjectOverridableAttribute : Attribute
{
    /// <summary>
    /// When true, this property uses deep-merge semantics via an ApplyOverrides method
    /// rather than simple replacement. The reflection engine reads the current value,
    /// invokes ApplyOverrides with the project override value, and assigns the result.
    /// </summary>
    public bool DeepMerge { get; init; }

    /// <summary>
    /// Explicit ordering for deterministic iteration. Must match the original
    /// ApplyProjectOverrides source order to preserve partial-apply-on-exception semantics.
    /// When an <see cref="ArgumentOutOfRangeException"/> is thrown mid-iteration, all
    /// properties with lower Order values are retained in the returned config.
    /// </summary>
    public int Order { get; init; }
}
