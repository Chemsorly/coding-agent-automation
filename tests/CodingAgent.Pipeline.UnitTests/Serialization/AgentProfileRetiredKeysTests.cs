using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using MessagePack;

namespace CodingAgent.Pipeline.UnitTests.Serialization;

/// <summary>
/// Spec 053 task 6.1: <see cref="AgentProfile"/> keys 2 (MatchLabels), 3 (AgentProviderConfigId) and 5 (Priority) are
/// retired. A profile serialized with values in them, as the release before spec 053 wrote it, still deserializes, and
/// every member that stays is kept. The payload is written by hand (as in <see cref="RetiredKeysDeserializationTests"/>)
/// because a frozen <c>[MessagePackObject]</c> copy in this assembly would clash with the generated resolver.
/// </summary>
public sealed class AgentProfileRetiredKeysTests
{
    private static readonly MessagePackSerializerOptions MsgPackOptions = MessagePackSerializerOptions.Standard;

    [Fact]
    public void MessagePack_AProfileWithTheRetiredKeys_Deserializes_KeepingTheOtherMembers()
    {
        var current = new AgentProfile
        {
            Id = "p-legacy",
            DisplayName = "Kiro dotnet",
            Enabled = false,
            McpServers = [new McpServerConfig { Name = "context7", Command = "uvx" }],
            JobTemplate = "dotnet,kiro",
        };
        var baseBytes = MessagePackSerializer.Serialize(current, MsgPackOptions);

        var reader = new MessagePackReader(baseBytes);
        var arrayLength = reader.ReadArrayHeader();
        arrayLength.Should().BeGreaterThan(5);
        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(bufferWriter);
        writer.WriteArrayHeader(arrayLength);
        for (var i = 0; i < arrayLength; i++)
        {
            var before = reader.Consumed;
            reader.Skip();
            var after = reader.Consumed;
            switch (i)
            {
                case 2: // MatchLabels
                    writer.WriteArrayHeader(2);
                    writer.Write("dotnet");
                    writer.Write("kiro");
                    break;
                case 3: // AgentProviderConfigId
                    writer.Write("agent-config");
                    break;
                case 5: // Priority
                    writer.Write(7);
                    break;
                default:
                    writer.WriteRaw(baseBytes.AsSpan()[(int)before..(int)after]);
                    break;
            }
        }

        writer.Flush();

        var profile = MessagePackSerializer.Deserialize<AgentProfile>(bufferWriter.WrittenMemory.ToArray(), MsgPackOptions);

        profile.Should().BeEquivalentTo(current);
    }
}
