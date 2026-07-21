using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for validating DI wiring correctness at application startup.
/// Fails fast if critical service registrations are incorrect.
/// </summary>
internal static class DiWiringValidationExtensions
{
    /// <summary>
    /// Validates that DI registrations resolve to the expected implementations.
    /// In DB mode, asserts that <see cref="IPipelineRunHistoryService"/> resolves to
    /// <see cref="PostgresPipelineRunHistoryService"/>. Throws <see cref="InvalidOperationException"/>
    /// if the wrong implementation is registered.
    /// </summary>
    /// <remarks>
    /// Should run after <see cref="DatabaseStartupExtensions.InitializeDatabaseAsync"/> to ensure
    /// DB mode is fully initialized. No dependency on other startup extension methods.
    /// </remarks>
    public static WebApplication ValidateDiWiring(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // TODO: Connection string is re-resolved from app.Configuration here rather than using the
        // pre-computed dbConnectionString local from Program.cs. These should always match, but if
        // configuration sources are added between Build() and this call, behavior could diverge.
        // Consider accepting dbConnectionString as a parameter for consistency. (review-findings)
        var connectionString = DatabaseConnectionResolver.Resolve(app.Configuration);
        if (string.IsNullOrEmpty(connectionString))
            return app; // Legacy mode — no DB-specific DI assertions needed

        var historyService = app.Services.GetRequiredService<IPipelineRunHistoryService>();
        if (historyService is not PostgresPipelineRunHistoryService)
            throw new InvalidOperationException(
                $"DB mode requires PostgresPipelineRunHistoryService but resolved {historyService.GetType().Name}. " +
                "Check DI registration order in Program.cs.");

        return app;
    }
}
