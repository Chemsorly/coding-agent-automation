using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Marks a property on <see cref="PipelineConfiguration"/> as overridable by
/// <see cref="PipelineProject"/>. Used by the reflection-based override engine in
/// <see cref="PipelineConfigurationResolver.ApplyProjectOverrides"/> to iterate overridable
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
    /// Explicit ordering for deterministic iteration. Ensures consistent logging
    /// (which property triggers an error first) and predictable override application.
    /// </summary>
    public int Order { get; init; }
}
