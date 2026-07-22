namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for provider configuration IDs.
/// Prevents accidental transposition of string parameters in method signatures.
/// </summary>
public readonly record struct ProviderConfigId(string Value)
{
    public static implicit operator ProviderConfigId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public override string ToString() => Value;
}
