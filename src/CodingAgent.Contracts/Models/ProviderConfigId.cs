namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for provider configuration IDs.
/// Prevents accidental transposition of string parameters in method signatures.
/// </summary>
/// <remarks>
/// <para><b>Construction invariant:</b> <c>new ProviderConfigId(value)</c> and the implicit <c>(ProviderConfigId)string</c>
/// operator both reject null and empty strings via <see cref="ArgumentException.ThrowIfNullOrEmpty"/>.
/// <c>default(ProviderConfigId)</c> is exempt — C# struct zero-initialization produces <c>Value = null</c>
/// and cannot be prevented. Use <c>default(ProviderConfigId)</c> only as a sentinel/unset value,
/// never as a valid provider config identifier.</para>
/// </remarks>
public readonly record struct ProviderConfigId
{
    // TODO: The init accessor allows object-initializer (`new ProviderConfigId { Value = "" }`) and with-expression
    // (`someProviderConfigId with { Value = "" }`) bypass paths that skip the constructor guard. These paths existed
    // before this change (the original positional primary constructor also synthesized an init accessor),
    // so this is a pre-existing limitation, not a regression. Closing it would require removing the init
    // accessor, which may affect JSON/serialization deserialization paths that rely on init-setters.
    public string Value { get; init; }

    public ProviderConfigId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    public static implicit operator ProviderConfigId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public override string ToString() => Value;
}
