namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for provider configuration IDs.
/// Prevents accidental transposition of string parameters in method signatures.
/// </summary>
public readonly record struct ProviderConfigId(string Value)
{
    // TODO: Consider adding null/empty validation (e.g., ArgumentException.ThrowIfNullOrEmpty(value))
    // in the implicit conversion operator. Currently, null strings are silently wrapped into a
    // ProviderConfigId with Value = null, which can cause NullReferenceExceptions downstream
    // (e.g., in GetProviderConfigByIdAsync). All current callers validate at a higher level,
    // but defense-in-depth is lost at this boundary.
    public static implicit operator ProviderConfigId(string value) => new(value);
    public override string ToString() => Value;
}
