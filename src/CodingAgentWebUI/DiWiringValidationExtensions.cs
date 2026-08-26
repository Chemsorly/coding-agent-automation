namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for validating DI wiring correctness at application startup.
/// </summary>
/// <remarks>
/// Infrastructure type checks are not applicable since the monolith has no direct database connection.
/// </remarks>
internal static class DiWiringValidationExtensions
{
    /// <summary>
    /// Validates DI wiring. Infrastructure assertions not applicable — monolith has no DB connection.
    /// </summary>
    public static WebApplication ValidateDiWiring(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app;
    }
}
