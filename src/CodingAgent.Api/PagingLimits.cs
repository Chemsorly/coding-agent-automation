namespace CodingAgent.Api;

/// <summary>
/// Spec 052 Req 14.5 (T9): the bounds of a paged read. A page below 1, a page size below 1 or above the store's
/// maximum, or a page whose offset leaves the <see cref="int"/> range answers 400 with the reason. Nothing is
/// clamped, so a caller never gets a different page than it asked for.
/// </summary>
internal static class PagingLimits
{
    /// <summary>Null when the values are in bounds, else the 400 answer. A null <paramref name="pageSize"/> checks the page only.</summary>
    public static IResult? Validate(int page, int? pageSize = null, int maxPageSize = int.MaxValue)
    {
        if (page < 1)
            return TypedResults.BadRequest($"page must be 1 or more, not {page}.");
        if (pageSize is not { } size)
            return null;
        if (size < 1 || size > maxPageSize)
            return TypedResults.BadRequest($"pageSize must be between 1 and {maxPageSize}, not {size}.");
        if ((long)(page - 1) * size > int.MaxValue)
            return TypedResults.BadRequest($"page {page} of size {size} is beyond the last possible page.");
        return null;
    }
}
