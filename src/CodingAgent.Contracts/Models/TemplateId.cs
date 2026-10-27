namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for pipeline job template IDs.
/// Prevents accidental transposition of string parameters in method signatures
/// (e.g., IProjectStore.MoveTemplateAsync has 3 consecutive string params: sourceProjectId, targetProjectId, templateId).
/// </summary>
/// <remarks>
/// <para><b>Construction invariant:</b> <c>new TemplateId(value)</c> and the implicit <c>(TemplateId)string</c>
/// operator both reject null and empty strings via <see cref="ArgumentException.ThrowIfNullOrEmpty"/>.
/// <c>default(TemplateId)</c> is exempt — C# struct zero-initialization produces <c>Value = null</c>
/// and cannot be prevented. Use <c>default(TemplateId)</c> only as a sentinel/unset value,
/// never as a valid template identifier.</para>
/// </remarks>
public readonly record struct TemplateId
{
    // TODO: The init accessor allows object-initializer (`new TemplateId { Value = "" }`) and with-expression
    // (`someTemplateId with { Value = "" }`) bypass paths that skip the constructor guard. These paths existed
    // before this change (the original positional primary constructor also synthesized an init accessor),
    // so this is a pre-existing limitation, not a regression. Closing it would require removing the init
    // accessor, which may affect JSON/serialization deserialization paths that rely on init-setters.
    public string Value { get; init; }

    public TemplateId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    public static implicit operator TemplateId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public override string ToString() => Value;
}
