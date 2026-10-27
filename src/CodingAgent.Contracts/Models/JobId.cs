using MessagePack;
using MessagePack.Formatters;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for job IDs used in SignalR hub method signatures.
/// Prevents accidental transposition of string parameters (jobId vs runId vs templateId).
/// </summary>
/// <remarks>
/// <para><b>Construction invariant:</b> <c>new JobId(value)</c> and the implicit <c>(JobId)string</c>
/// operator both reject null and empty strings via <see cref="ArgumentException.ThrowIfNullOrEmpty"/>.
/// <c>default(JobId)</c> is exempt — C# struct zero-initialization produces <c>Value = null</c>
/// and cannot be prevented. Use <c>default(JobId)</c> only as a sentinel/unset value,
/// never as a valid job identifier.</para>
/// </remarks>
public readonly record struct JobId
{
    // TODO: The init accessor allows object-initializer (`new JobId { Value = "" }`) and with-expression
    // (`someJobId with { Value = "" }`) bypass paths that skip the constructor guard. These paths existed
    // before this change (the original positional primary constructor also synthesized an init accessor),
    // so this is a pre-existing limitation, not a regression. Closing it would require removing the init
    // accessor, which may affect JSON/serialization deserialization paths that rely on init-setters.
    public string Value { get; init; }

    public JobId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    public static implicit operator JobId(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(value);
    }

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// Custom MessagePack formatter that serializes <see cref="JobId"/> as a bare string
/// on the wire, maintaining wire compatibility with existing string-based callers.
/// Without this formatter, <c>ContractlessStandardResolver</c> would serialize the struct
/// as a map <c>{"Value":"..."}</c> instead of a plain string.
/// </summary>
public sealed class JobIdFormatter : IMessagePackFormatter<JobId>
{
    public void Serialize(ref MessagePackWriter writer, JobId value, MessagePackSerializerOptions options)
    {
        if (value.Value is null)
            throw new MessagePackSerializationException("JobId cannot serialize a null Value (e.g., default(JobId)).");
        writer.Write(value.Value);
    }

    public JobId Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var value = reader.ReadString();
        if (value is null)
            throw new MessagePackSerializationException("JobId cannot be deserialized from a nil token.");
        if (string.IsNullOrEmpty(value))
            throw new MessagePackSerializationException("JobId cannot be deserialized from an empty string.");
        return new(value);
    }
}
