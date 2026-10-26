namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for pipeline project IDs.
/// Prevents accidental transposition of string parameters in method signatures
/// (e.g., IProjectStore.MoveTemplateAsync has two adjacent project-id parameters:
/// sourceProjectId and targetProjectId, which are silently interchangeable as plain strings).
/// </summary>
/// <remarks>
/// <para><b>Construction invariant:</b> <c>new ProjectId(value)</c> and the implicit <c>(ProjectId)string</c>
/// operator both reject null and empty strings via <see cref="ArgumentException.ThrowIfNullOrEmpty"/>.
/// <c>default(ProjectId)</c> is exempt — C# struct zero-initialization produces <c>Value = null</c>
/// and cannot be prevented. Use <c>default(ProjectId)</c> only as a sentinel/unset value,
/// never as a valid project identifier.</para>
/// </remarks>
public readonly record struct ProjectId
{
    // TODO: The init accessor allows object-initializer (`new ProjectId { Value = "" }`) and with-expression
    // (`someProjectId with { Value = "" }`) bypass paths that skip the constructor guard. These paths existed
    // before this change (the original positional primary constructor also synthesized an init accessor),
    // so this is a pre-existing limitation, not a regression. Closing it would require removing the init
    // accessor, which may affect JSON/serialization deserialization paths that rely on init-setters.
    public string Value { get; init; }

    public ProjectId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    public static implicit operator ProjectId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public override string ToString() => Value;
}
