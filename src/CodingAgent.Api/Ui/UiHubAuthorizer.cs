using System.Security.Claims;
using CodingAgent.AgentGateway;
using CodingAgent.Api.Chat;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Contracts.Auth;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 D7: who may watch what on the <see cref="UiHub"/>. A run is checked against the project of its stored
/// work item (a run's ID is its work item's), a chat against its session record, the same way the chat endpoints
/// check it.
/// </summary>
internal sealed class UiHubAuthorizer(IDbContextFactory<PipelineDbContext> dbFactory, ChatSessionStore sessions) : IUiHubAuthorizer
{
    public async Task<bool> CanWatchRunAsync(ClaimsPrincipal user, Guid runId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.WorkItems.AsNoTracking()
            .Where(w => w.Id == runId)
            .Select(w => new { w.ProjectId })
            .FirstOrDefaultAsync(ct);
        return item is not null && UserAccess.For(user).CanRead(ProjectIds.Normalize(item.ProjectId));
    }

    public async Task<bool> CanWatchChatAsync(ClaimsPrincipal user, string agentId, CancellationToken ct) =>
        await UiChatEndpoints.FindOwnedAsync(agentId, UserAccess.For(user), sessions) is not null;
}
