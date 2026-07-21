using MessagePack;
using MessagePack.Formatters;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for job IDs used in SignalR hub method signatures.
/// Prevents accidental transposition of string parameters (jobId vs runId vs templateId).
/// </summary>
/// <remarks>
/// <c>default(JobId)</c> has <c>Value = null</c> because struct defaults bypass constructors
/// and operators. The null guards on the implicit conversion and MessagePack formatter protect
/// explicit creation paths but cannot prevent <c>default(T)</c> from existing.
/// </remarks>
public readonly record struct JobId(string Value)
{
    public static implicit operator JobId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value);
    }

    public override string ToString() => Value;
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
        return new(value);
    }
}
