namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for validating DI wiring correctness at application startup.
/// </summary>
/// <remarks>
/// DB-mode DI assertions removed in Spec 045 Task 10: the monolith no longer has
/// a Postgres connection, so all Infrastructure type checks are no longer applicable.
/// </remarks>
internal static class DiWiringValidationExtensions
{
    /// <summary>
    /// Validates DI wiring. DB-mode assertions removed in Spec 045 (no DB in monolith).
    /// </summary>
    public static WebApplication ValidateDiWiring(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app;
    }
}
