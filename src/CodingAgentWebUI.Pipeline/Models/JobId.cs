using MessagePack;
using MessagePack.Formatters;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Strongly-typed wrapper for job IDs used in SignalR hub method signatures.
/// Prevents accidental transposition of string parameters (jobId vs runId vs templateId).
/// </summary>
public readonly record struct JobId(string Value)
{
    // TODO: Consider adding a null guard (e.g., `?? throw new ArgumentNullException(nameof(value))`)
    // to prevent silent creation of a JobId with a null inner value, which could cause
    // NullReferenceException downstream in ToString(), string comparisons, and logging.
    public static implicit operator JobId(string value) => new(value);
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
    // TODO: Add null guard for value.Value — default(JobId) has null Value,
    // and writer.Write(null) writes Nil which round-trips to JobId(null) via Deserialize.
    public void Serialize(ref MessagePackWriter writer, JobId value, MessagePackSerializerOptions options)
        => writer.Write(value.Value);

    // TODO: Add null guard — ReadString() can return null for MessagePack nil tokens.
    // Consider: `reader.ReadString() ?? throw new MessagePackSerializationException("JobId cannot be null")`
    public JobId Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        => new(reader.ReadString()!);
}
