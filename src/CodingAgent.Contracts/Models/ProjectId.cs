namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for pipeline project IDs.
/// Prevents accidental transposition of string parameters in method signatures
/// (e.g., IProjectStore.MoveTemplateAsync has two adjacent project-id parameters:
/// sourceProjectId and targetProjectId, which are silently interchangeable as plain strings).
/// </summary>
// TODO: The primary constructor `new ProjectId(value)` bypasses ThrowIfNullOrEmpty validation.
// Code that reconstructs ProjectId via the constructor (e.g., key paths in services) could
// create ProjectId("") if the persisted value is empty. Consider adding validation to the
// constructor body if this pattern emerges.
// TODO: [WARNING] The constructor/default bypass creates an observable inconsistency: `new ProjectId("")`
// silently succeeds while `(ProjectId)""` throws, and `default(ProjectId).Value` is null. Call sites
// using `PipelineApiConfigClient.MoveTemplateAsync` pass `.Value` directly into the JSON body, meaning
// a default/empty ProjectId would produce a null field in the request rather than failing fast.
// All strongly-typed ID types share this gap (see TemplateId TODO). A follow-up phase should add
// constructor body validation (ArgumentException.ThrowIfNullOrEmpty(value)) to all of them.
public readonly record struct ProjectId(string Value)
{
    public static implicit operator ProjectId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public override string ToString() => Value;
}
