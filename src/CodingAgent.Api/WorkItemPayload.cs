using System.Text.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api;

/// <summary>
/// Shared helper for deserializing <see cref="JobDistributionRequest"/> payloads stored in
/// the <c>WorkItems.Payload</c> column.
/// <para>
/// Centralizes the deserialization pattern to eliminate the Default-vs-Lenient options
/// inconsistency that previously existed across the four call sites:
/// <c>GetPendingWorkItems</c>, <c>GetActiveWorkItems</c>, <c>GetAssignment</c>, and
/// <c>PostLabelSwap</c>. All four now route through this helper.
/// </para>
/// </summary>
internal static class WorkItemPayload
{
    /// <summary>
    /// Attempts to deserialize a <see cref="JobDistributionRequest"/> from a raw JSON payload string.
    /// Uses <see cref="PipelineJsonOptions.Lenient"/> (<c>PropertyNameCaseInsensitive = true</c>)
    /// so that legacy PascalCase payloads (written before camelCase serialization was enforced)
    /// are parsed correctly by all endpoints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <c>false</c> when:
    /// <list type="bullet">
    ///   <item><paramref name="payload"/> is <c>null</c></item>
    ///   <item>The JSON is malformed (catches <see cref="JsonException"/>)</item>
    ///   <item>The JSON deserializes to <c>null</c> (e.g., the literal <c>"null"</c>)</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Note on options asymmetry:</b> <see cref="PipelineJsonOptions.Lenient"/> does not include
    /// <c>TimeSpanJsonConverter</c> (unlike <see cref="PipelineJsonOptions.Default"/>). This is safe
    /// for <see cref="JobDistributionRequest"/> because it has no <c>TimeSpan</c> fields
    /// (<c>TimeoutSeconds</c> is <c>int</c>). If a <c>TimeSpan</c> property is ever added to this
    /// record, <c>Lenient</c> must be updated accordingly.
    /// </para>
    // TODO [WARNING]: The constraint that JobDistributionRequest must not have TimeSpan fields is
    // enforced only by this comment. If a TimeSpan property is ever added to that record, Lenient
    // will silently misparse it at runtime (TimeSpanJsonConverter is absent from Lenient). Update
    // PipelineJsonOptions.Lenient to include TimeSpanJsonConverter if that happens. (Issue #2776)
    /// </remarks>
    /// <param name="payload">The raw JSON string from <c>WorkItems.Payload</c>. May be <c>null</c>.</param>
    /// <param name="req">
    /// When this method returns <c>true</c>, contains the deserialized request; otherwise <c>null</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if deserialization succeeded and produced a non-null result; <c>false</c> otherwise.
    /// </returns>
    internal static bool TryDeserialize(string? payload, out JobDistributionRequest? req)
    {
        req = null;
        if (payload is null) return false;
        try
        {
            req = JsonSerializer.Deserialize<JobDistributionRequest>(payload, PipelineJsonOptions.Lenient);
            return req is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
