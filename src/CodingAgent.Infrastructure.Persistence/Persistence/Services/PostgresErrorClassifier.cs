using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Infrastructure.Persistence.Services;

/// <summary>
/// Centralised classifier for PostgreSQL unique-constraint violations (SQLSTATE 23505).
/// <para>
/// Uses reflection to access <c>PostgresException.SqlState</c> so this assembly retains its
/// no-Npgsql-compile-dependency property. Callers that hold a <c>DbUpdateException</c>
/// may pass it directly — the implicit upcast to <see cref="Exception"/> is correct.
/// </para>
/// </summary>
public static class PostgresErrorClassifier
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="ex"/> represents a PostgreSQL
    /// unique-constraint violation (SQLSTATE 23505), the EF-InMemory equivalent, or a generic
    /// duplicate-key / unique-constraint message from any provider.
    /// </summary>
    public static bool IsUniqueViolation(Exception ex)
    {
        // TODO: Add ArgumentNullException.ThrowIfNull(ex) guard. All current call sites pass
        // a caught (non-null) exception via 'when (IsUniqueViolation(ex))' filters, so null
        // cannot occur today; however, as a public method the contract should be enforced
        // explicitly to avoid a NullReferenceException at ex.Message on line ~43 if a future
        // caller passes null. (DotNetSpecialist review warning)

        // Reflection-based path: avoids a compile-time dependency on Npgsql in the Services layer.
        // In production, EF Core wraps Npgsql's PostgresException inside a DbUpdateException.
        if (ex is DbUpdateException dbEx)
        {
            var inner = dbEx.InnerException;
            if (inner is not null && inner.GetType().Name == "PostgresException")
            {
                // TODO: Specify BindingFlags.Public | BindingFlags.Instance on GetProperty to make
                // the intent explicit and document the assumption that SqlState is a public instance
                // property. If a future Npgsql version makes SqlState non-public or interface-
                // implemented, the current call returns null and silently falls through to the
                // message-based fallback rather than failing visibly. (DotNetSpecialist review warning)
                var sqlStateProp = inner.GetType().GetProperty("SqlState");
                if (sqlStateProp?.GetValue(inner) is string sqlState)
                    // TODO: The SqlState check short-circuits here and does NOT fall through to the
                    // message-based checks below. This matches the prior behaviour of the two
                    // Persistence callers but is a silent behaviour change for the Api call site
                    // (WorkItemDispatchEndpoints), which previously had no PostgresException type
                    // check and always consulted message fallbacks. A PostgresException with a
                    // non-23505 SqlState whose message contains "duplicate key" (e.g., from a
                    // trigger) will now return false at the Api site where it previously returned
                    // true. Determine whether the short-circuit is intentional for all callers or
                    // whether the message fallback should also run when SqlState != "23505".
                    // (DotNetSpecialist review warning)
                    return sqlState == "23505";
            }
        }

        // Message-based fallback for all exception types:
        // • Generic fallback for non-Npgsql drivers or DbUpdateException without a PostgresException inner.
        // • EF InMemory throws ArgumentException("An item with the same key has already been added")
        //   directly (not wrapped in DbUpdateException), so the top-level message must also be checked.
        //   Note: the EF InMemory phrase is an implementation detail, not a public API contract.
        //   If EF Core changes this phrase in a future version, InMemory concurrent-insert races
        //   will produce 500 instead of 409. This is acceptable known behaviour.
        var message = ex.Message ?? "";
        var innerMessage = ex.InnerException?.Message ?? "";
        return message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            || innerMessage.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || innerMessage.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            || message.Contains("An item with the same key has already been added",
                StringComparison.OrdinalIgnoreCase);
    }
}
