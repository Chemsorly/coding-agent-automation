using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Minimal API endpoints for harness suggestion persistence.
/// Exposes GET/PUT so the orchestrator can delegate all DB access to the API.
/// All endpoints require AgentApiKey authentication (operator tier).
/// </summary>
public static class HarnessSuggestionEndpoints
{
    public static void MapHarnessSuggestionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/harness-suggestions")
            .RequireAuthorization(ApiAuthPolicies.Operator);

        group.MapGet("/", Get);
        group.MapPut("/", Save);
    }

    internal static async Task<IResult> Get(
        IHarnessSuggestionStore store,
        CancellationToken ct)
    {
        var suggestions = await store.GetAsync(ct);
        return suggestions is null ? TypedResults.NoContent() : TypedResults.Ok(suggestions);
    }

    internal static async Task<IResult> Save(
        [Microsoft.AspNetCore.Mvc.FromBody] HarnessSuggestions suggestions,
        IHarnessSuggestionStore store,
        CancellationToken ct)
    {
        await store.SaveAsync(suggestions, ct);
        return TypedResults.Ok();
    }
}
