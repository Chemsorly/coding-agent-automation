using System.Collections.Concurrent;
using System.Text.Json;
using CodingAgent.Orchestration.Redis;

namespace CodingAgent.Api.Chat;

/// <summary>
/// A chat pod launched through <c>/api/ui/chat</c> (spec 051 D5): who started it, and what each prompt
/// is built from. Only the owner (or an admin) may prompt, keep alive or end it.
/// </summary>
/// <param name="OwnerIdentitySource">The owner's identity source (token <c>idp</c>), with <paramref name="OwnerSubject"/> the user's key.</param>
/// <param name="ProjectId">The project whose MCP servers, secrets and steering the chat runs with, or null.</param>
/// <param name="ChatWindowId">Stable for the life of the pod; the agent keeps one conversation per window.</param>
/// <param name="Prompted">A prompt has been delivered, so later ones resume the conversation.</param>
/// <param name="Model">The model the chat was started with (spec 053 req 6.1); null for sessions stored before.</param>
/// <param name="Effort">The effort the chat was started with; null for sessions stored before.</param>
internal sealed record ChatSessionRecord(
    string AgentId,
    string? OwnerIdentitySource,
    string OwnerSubject,
    string? ProjectId,
    string ProfileId,
    string ChatWindowId,
    bool Prompted = false,
    string? Model = null,
    string? Effort = null);

/// <summary>
/// Holds <see cref="ChatSessionRecord"/>s by agent ID: in Redis when the API runs with it, so every
/// replica sees them, otherwise in memory. A record lives as long as a chat job may run, plus an hour;
/// ending the chat deletes it.
/// </summary>
internal sealed class ChatSessionStore(IRedisStore? redis, TimeSpan lifetime)
{
    private const string KeyPrefix = "chat-session:";

    private readonly ConcurrentDictionary<string, (ChatSessionRecord Record, DateTimeOffset ExpiresAt)> _local = new(StringComparer.Ordinal);

    public async Task SaveAsync(ChatSessionRecord record)
    {
        if (redis is not null)
        {
            await redis.SetAsync(KeyPrefix + record.AgentId, JsonSerializer.Serialize(record), lifetime);
            return;
        }
        _local[record.AgentId] = (record, DateTimeOffset.UtcNow + lifetime);
    }

    public async Task<ChatSessionRecord?> FindAsync(string agentId)
    {
        if (redis is not null)
        {
            var json = await redis.GetAsync(KeyPrefix + agentId);
            return json is null ? null : JsonSerializer.Deserialize<ChatSessionRecord>(json);
        }
        if (!_local.TryGetValue(agentId, out var entry))
            return null;
        if (entry.ExpiresAt > DateTimeOffset.UtcNow)
            return entry.Record;
        _local.TryRemove(agentId, out _);
        return null;
    }

    public async Task DeleteAsync(string agentId)
    {
        if (redis is not null)
        {
            await redis.DeleteAsync(KeyPrefix + agentId);
            return;
        }
        _local.TryRemove(agentId, out _);
    }
}
