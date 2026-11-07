using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Infrastructure.Persistence.Services;

/// <summary>
/// Spec 056 DD7, DD10: clears holds an operator ended, so the items regain their place in the dispatch order (operator
/// holds are fetched last). A resume clears its credential's <c>credential_paused</c> holds; saving a routing table
/// clears every operator hold. Both act on Pending items only.
/// </summary>
public static class PendingHoldResets
{
    /// <summary>Clears the <c>credential_paused</c> holds of <paramref name="credential"/>; returns how many.</summary>
    public static Task<int> ClearCredentialHoldsAsync(PipelineDbContext db, string credential, CancellationToken ct) =>
        ClearAsync(db, q => q.Where(w => w.HoldReason == HoldReasons.CredentialPaused && w.Credential == credential), ct);

    /// <summary>Clears every operator hold (<see cref="HoldReasons.OperatorHolds"/>); returns how many.</summary>
    public static Task<int> ClearOperatorHoldsAsync(PipelineDbContext db, CancellationToken ct)
    {
        var reasons = HoldReasons.OperatorHolds.ToList();
        return ClearAsync(db, q => q.Where(w => w.HoldReason != null && reasons.Contains(w.HoldReason!)), ct);
    }

    private static async Task<int> ClearAsync(
        PipelineDbContext db,
        Func<IQueryable<Entities.WorkItemEntity>, IQueryable<Entities.WorkItemEntity>> filter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        // A tracked update rather than ExecuteUpdate: the API tests run on EF InMemory, which has no bulk update.
        var held = await filter(db.WorkItems.Where(w => w.Status == WorkItemStatus.Pending)).ToListAsync(ct);
        foreach (var item in held)
        {
            item.HoldReason = null;
            item.HoldText = null;
            item.HeldAt = null;
        }
        if (held.Count > 0)
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // An item was dispatched or held again meanwhile; its new state stands.
            }
        }
        return held.Count;
    }
}

/// <summary>Spec 056 DD10: the first key of the dispatch order.</summary>
public static class WorkItemHoldOrdering
{
    /// <summary>
    /// Orders items held for an operator hold (<see cref="HoldReasons.OperatorHolds"/>: a paused credential or a route
    /// that cannot run) after every other item, so they never fill the dispatch window. The null check is needed: in
    /// Postgres <c>x = ANY(@p)</c> is NULL for a NULL <c>x</c>, and NULLs sort last, so a bare <c>Contains</c> would put
    /// every unheld item last.
    /// </summary>
    public static IOrderedQueryable<Entities.WorkItemEntity> OrderByOperatorHoldsLast(this IQueryable<Entities.WorkItemEntity> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var operatorHolds = HoldReasons.OperatorHolds.ToList();
        return source.OrderBy(w => w.HoldReason != null && operatorHolds.Contains(w.HoldReason!));
    }
}
