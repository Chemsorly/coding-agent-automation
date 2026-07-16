using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Default implementation of <see cref="IPipelineReporterFactory"/> that creates
/// <see cref="PipelineSignalRReporter"/> instances with the provided logger.
/// </summary>
// TODO: Add unit tests for PipelineReporterFactory.Create to verify it produces a correctly-configured PipelineSignalRReporter with the expected parameters. Currently no test validates this factory's behavior.
public sealed class PipelineReporterFactory : IPipelineReporterFactory
{
    private readonly Serilog.ILogger _logger;

    public PipelineReporterFactory(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public PipelineSignalRReporter Create(
        HubConnection connection,
        OutputBatcher outputBatcher,
        string jobId,
        PipelineRun run,
        Action<PipelineStep?>? onStepChanged)
    {
        return new PipelineSignalRReporter(connection, outputBatcher, jobId, run, onStepChanged, _logger);
    }
}
