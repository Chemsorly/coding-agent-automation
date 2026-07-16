using CodingAgentWebUI.Pipeline.Models;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

// TODO: Add unit tests for JobIdFormatter serialization roundtrip using JobIdMessagePackOptions.Create(),
// including the nil/null deserialization path (msgpack nil → ReadString() returns null → ArgumentException).

namespace CodingAgentWebUI.Pipeline.Serialization;

/// <summary>
/// Custom MessagePack formatter for <see cref="JobId"/> that serializes/deserializes it as a plain string.
/// This preserves wire-format compatibility — the MessagePack binary payload is identical to when
/// the parameter was a raw <c>string</c>.
/// </summary>
public sealed class JobIdFormatter : IMessagePackFormatter<JobId>
{
    public static readonly JobIdFormatter Instance = new();

    public void Serialize(ref MessagePackWriter writer, JobId value, MessagePackSerializerOptions options)
    {
        writer.Write(value.Value);
    }

    public JobId Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        // TODO: ReadString() returns string? — a msgpack nil from a malicious/buggy client will pass null
        // to the JobId constructor, throwing ArgumentException. Consider throwing MessagePackSerializationException
        // with a descriptive message ("JobId cannot be deserialized from nil") for clearer diagnostics.
        var value = reader.ReadString();
        return new JobId(value!);
    }
}

/// <summary>
/// Resolver that provides <see cref="JobIdFormatter"/> for <see cref="JobId"/> types.
/// Used as the first resolver in a composite chain so SignalR can serialize/deserialize
/// <see cref="JobId"/> parameters as plain strings on the wire.
/// </summary>
public sealed class JobIdFormatterResolver : IFormatterResolver
{
    public static readonly JobIdFormatterResolver Instance = new();

    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        if (typeof(T) == typeof(JobId))
            return (IMessagePackFormatter<T>)(object)JobIdFormatter.Instance;
        return null;
    }
}

/// <summary>
/// Provides the production <see cref="MessagePackSerializerOptions"/> that includes
/// <see cref="JobIdFormatterResolver"/> ahead of the default SignalR resolvers.
/// Use this in all <c>AddMessagePackProtocol()</c> call sites to ensure consistent
/// serialization of <see cref="JobId"/> across server, agent, and test clients.
/// </summary>
public static class JobIdMessagePackOptions
{
    /// <summary>
    /// Configures MessagePack protocol options to include <see cref="JobIdFormatterResolver"/>
    /// as the first resolver, falling through to <see cref="ContractlessStandardResolverAllowPrivate"/>
    /// for all other types.
    /// </summary>
    public static MessagePackSerializerOptions Create()
    {
        var resolver = MessagePack.Resolvers.CompositeResolver.Create(
            JobIdFormatterResolver.Instance,
            ContractlessStandardResolverAllowPrivate.Instance);

        return MessagePackSerializerOptions.Standard.WithResolver(resolver);
    }
}
