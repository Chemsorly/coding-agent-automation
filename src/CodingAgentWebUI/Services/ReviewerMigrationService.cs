using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Startup migration service that ensures at least one ReviewerConfiguration exists.
/// If no ReviewerConfigs are found in the configuration store AND the legacy
/// CodeReviewConfiguration.Agents field is populated, creates a default "migrated"
/// ReviewerConfiguration sourced from the legacy agents.
/// Runs to completion in StartAsync (not a long-running BackgroundService).
/// Registered before JobQueueDrainService to ensure ReviewerConfigs exist before the first dispatch.
/// </summary>
public sealed class ReviewerMigrationService : IHostedService
{
    private readonly IConfigurationStore _configStore;
    private readonly ILogger _logger;

    public ReviewerMigrationService(IConfigurationStore configStore, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(logger);

        _configStore = configStore;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Precondition 1: No ReviewerConfigurations exist yet
            var existingConfigs = await _configStore.LoadReviewerConfigsAsync(cancellationToken);

            if (existingConfigs.Count > 0)
            {
                _logger.Debug("ReviewerMigration: {Count} ReviewerConfiguration(s) already exist, skipping migration", existingConfigs.Count);
                return;
            }

            // Precondition 2: Pipeline config exists and can be loaded
            var pipelineConfig = await _configStore.LoadPipelineConfigAsync(cancellationToken);

            // Precondition 3: Legacy CodeReview.Agents is populated
            #pragma warning disable CS0618 // Obsolete — reading legacy field for migration
            var legacyAgents = pipelineConfig.CodeReview.Agents;
            #pragma warning restore CS0618

            if (legacyAgents is not { Count: > 0 })
            {
                _logger.Debug("ReviewerMigration: No legacy CodeReview.Agents found, skipping migration");
                return;
            }

            // All preconditions met — create default ReviewerConfiguration from legacy agents
            var migratedConfig = new ReviewerConfiguration
            {
                DisplayName = "Default Reviewers (Migrated)",
                MatchLabels = [],
                Enabled = true,
                ExecutionOrder = 0,
                Agents = legacyAgents.Select(a => new ReviewAgent
                {
                    Name = a.Name,
                    Prompt = a.Prompt
                }).ToList()
            };

            await _configStore.SaveReviewerConfigAsync(migratedConfig, cancellationToken);

            _logger.Information(
                "ReviewerMigration: Created default ReviewerConfiguration '{DisplayName}' (Id={Id}) with {AgentCount} agent(s) migrated from CodeReview.Agents",
                migratedConfig.DisplayName,
                migratedConfig.Id,
                migratedConfig.Agents.Count);
        }
        catch (Exception ex)
        {
            // Migration failure is non-fatal — log error and continue startup
            _logger.Error(ex, "ReviewerMigration: Failed to create default ReviewerConfiguration during startup migration");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
