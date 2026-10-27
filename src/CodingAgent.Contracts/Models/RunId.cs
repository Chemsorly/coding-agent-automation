namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for pipeline run IDs.
/// Prevents accidental transposition of string parameters in method signatures
/// (e.g., RunLifecycleManager.AgentAcceptedRunAsync has 3 consecutive string params).
/// </summary>
/// <remarks>
/// <para><b>Construction invariant:</b> <c>new RunId(value)</c> and the implicit <c>(RunId)string</c>
/// operator both reject null and empty strings via <see cref="ArgumentException.ThrowIfNullOrEmpty"/>.
/// <c>default(RunId)</c> is exempt — C# struct zero-initialization produces <c>Value = null</c>
/// and cannot be prevented. Use <c>default(RunId)</c> only as a sentinel/unset value,
/// never as a valid run identifier.</para>
/// </remarks>
public readonly record struct RunId
{
    // TODO: The init accessor allows object-initializer (`new RunId { Value = "" }`) and with-expression
    // (`someRunId with { Value = "" }`) bypass paths that skip the constructor guard. These paths existed
    // before this change (the original positional primary constructor also synthesized an init accessor),
    // so this is a pre-existing limitation, not a regression. Closing it would require removing the init
    // accessor, which may affect JSON/serialization deserialization paths that rely on init-setters.
    public string Value { get; init; }

    public RunId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    public static implicit operator RunId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public override string ToString() => Value ?? string.Empty;
}
