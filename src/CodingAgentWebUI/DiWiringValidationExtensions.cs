namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for validating DI wiring correctness at application startup.
/// </summary>
internal static class DiWiringValidationExtensions
{
    /// <summary>
    /// Validates critical DI wiring at startup. Resolves key services to confirm they
    /// are registered and constructable — fails fast rather than producing confusing runtime errors.
    /// </summary>
    public static WebApplication ValidateDiWiring(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Validate services critical to the dispatch + pipeline loop path.
        // GetRequiredService throws InvalidOperationException if the service is not registered,
        // which ASP.NET Core surfaces as a startup failure with a clear message.
        _ = app.Services.GetRequiredService<Pipeline.Interfaces.IDispatchOrchestrationService>();
        _ = app.Services.GetRequiredService<Pipeline.Interfaces.IWorkDistributor>();
        _ = app.Services.GetRequiredService<Pipeline.Interfaces.IPipelineRunHistoryService>();

        return app;
    }
}
