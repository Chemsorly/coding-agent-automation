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

    /// <summary>
    /// Clears every operator hold (<see cref="HoldReasons.OperatorHolds"/>) except the <c>credential_paused</c> holds of
    /// <paramref name="pausedCredentials"/> (still paused: clearing them would put their items back in front, where the
    /// Scheduler skips them without holding them again, req 3.5); returns how many.
    /// </summary>
    public static Task<int> ClearOperatorHoldsAsync(
        PipelineDbContext db, CancellationToken ct, IReadOnlyCollection<string>? pausedCredentials = null)
    {
        var reasons = HoldReasons.OperatorHolds.ToList();
        var keep = (pausedCredentials ?? []).ToList();
        return ClearAsync(db, q => q.Where(w => w.HoldReason != null && reasons.Contains(w.HoldReason)
            && !(w.HoldReason == HoldReasons.CredentialPaused && w.Credential != null && keep.Contains(w.Credential))), ct);
    }

    /// <summary>
    /// Spec 056 req 3.5(a): holds every Pending item whose last resolution was <paramref name="credential"/> with
    /// <c>credential_paused</c> and <paramref name="text"/>, at once, so all of them sort behind other work from the next
    /// cycle on instead of one per cycle. Items held for another operator hold keep it; an item already held with the same
    /// text is not written. Returns how many changed.
    /// </summary>
    public static async Task<int> HoldCredentialAsync(
        PipelineDbContext db, string credential, string text, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var cut = HoldTexts.Cut(text);
        var others = HoldReasons.OperatorHolds.Where(r => r != HoldReasons.CredentialPaused).ToList();
        var items = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Pending && w.Credential == credential)
            .Where(w => w.HoldReason == null || !others.Contains(w.HoldReason))
            .Where(w => w.HoldReason != HoldReasons.CredentialPaused || w.HoldText != cut)
            .ToListAsync(ct);
        foreach (var item in items)
        {
            if (item.HoldReason != HoldReasons.CredentialPaused || item.HeldAt is null)
                item.HeldAt = now;
            item.HoldReason = HoldReasons.CredentialPaused;
            item.HoldText = cut;
        }
        if (items.Count > 0)
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // An item was dispatched meanwhile; the next dispatch attempt holds the rest.
                return 0;
            }
        }
        return items.Count;
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
        return source.OrderBy(w => w.HoldReason != null && operatorHolds.Contains(w.HoldReason));
    }
}
