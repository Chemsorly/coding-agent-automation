using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Startup migration service that ensures at least one QualityGateConfiguration exists.
/// If no QGCs are found in the configuration store, creates a default "migrated" QGC
/// sourced from PipelineConfiguration values and hardcoded dotnet build/test commands.
/// Runs to completion in StartAsync (not a long-running BackgroundService).
/// Registered before JobQueueDrainService to ensure QGCs exist before the first dispatch.
/// </summary>
public sealed class QualityGateMigrationService : IHostedService
{
    private readonly IConfigurationStore _configStore;
    private readonly ILogger _logger;

    public QualityGateMigrationService(IConfigurationStore configStore, ILogger logger)
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
            var existingQgcs = await _configStore.LoadQualityGateConfigsAsync(cancellationToken);

            if (existingQgcs.Count > 0)
            {
                _logger.Debug("QualityGateMigration: {Count} QGC(s) already exist, skipping migration", existingQgcs.Count);
                return;
            }

            // No QGCs exist — create a default one with standard .NET defaults
            var defaultQgc = new QualityGateConfiguration
            {
                DisplayName = "Default (migrated)",
                MatchLabels = [],
                CompilationCommand = "dotnet",
                CompilationArguments = ["build", "--no-restore"],
                TestCommand = "dotnet",
                TestArguments = ["test", "--no-restore", "--no-build"],
                CoverageThreshold = 50.0,
                SecurityScanEnabled = true,
                Enabled = true,
                ExecutionOrder = 0
            };

            await _configStore.SaveQualityGateConfigAsync(defaultQgc, cancellationToken);

            _logger.Information(
                "QualityGateMigration: Created default QGC '{DisplayName}' (Id={Id}) with CoverageThreshold={Threshold}, SecurityScanEnabled={SecurityScan}",
                defaultQgc.DisplayName,
                defaultQgc.Id,
                defaultQgc.CoverageThreshold,
                defaultQgc.SecurityScanEnabled);
        }
        catch (Exception ex)
        {
            // Migration failure is non-fatal — log error and continue startup
            _logger.Error(ex, "QualityGateMigration: Failed to create default QGC during startup migration");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
