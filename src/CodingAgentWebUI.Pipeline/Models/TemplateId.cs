namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for pipeline job template IDs.
/// Prevents accidental transposition of string parameters in method signatures
/// (e.g., IProjectStore.MoveTemplateAsync has 3 consecutive string params: sourceProjectId, targetProjectId, templateId).
/// </summary>
// TODO: The primary constructor `new TemplateId(value)` bypasses ThrowIfNullOrEmpty validation.
// Code that reconstructs TemplateId via the constructor (e.g., ConcurrentDictionary key paths in
// ConsolidationService) could create TemplateId("") if the persisted value is empty, leading to
// key mismatches in _runningRuns. Consider adding validation to the constructor body.
public readonly record struct TemplateId(string Value)
{
    public static implicit operator TemplateId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public override string ToString() => Value;
}
