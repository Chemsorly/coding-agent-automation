using CodingAgent.Api.Client;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using ILeaderGate = CodingAgent.Pipeline.Interfaces.ILeaderGate;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.Services;

/// <summary>
/// Leader-side <see cref="BackgroundService"/> that polls <c>scheduler:loop-command</c> every
/// <paramref name="interval"/> (default 1 s) and executes the command via
/// <see cref="ILoopCommandExecutor"/> when this pod is the leader.
/// Writes the result to <c>scheduler:loop-command-result:{id}</c> so the non-leader relay can
/// return it to the caller.
/// <para>
/// The idempotency guard (<c>_lastHandledId</c>) prevents a command from being executed more than
/// once even if the leader restarts and picks up the same key within its 60 s TTL.
/// </para>
/// <para>
/// Only registered in DI when Redis is configured — see
/// <c>SchedulerServiceCollectionExtensions.AddSchedulerServices</c>.
/// </para>
/// </summary>
public sealed class LoopCommandHandlerService : BackgroundService
{
    private readonly ILoopCommandExecutor _executor;
    private readonly IPipelineLoopService _loopService;
    private readonly IPipelineApiConfigClient _configClient;
    private readonly IRedisStore _store;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;

    // NOTE (issue #3552): _lastHandledId is accessed only from the HandlePendingCommandAsync call
    // path, which is always invoked sequentially by PeriodicTimer — never concurrently. It is
    // therefore safe without volatile or locking. If ExecuteAsync is ever changed to run ticks
    // concurrently (e.g. via Task.Run), this field would become a data race. Keep this invariant
    // and update this comment if that changes (see .NET Specialist review).
    private string? _lastHandledId;

    public LoopCommandHandlerService(
        ILoopCommandExecutor executor,
        IPipelineLoopService loopService,
        IPipelineApiConfigClient configClient,
        IRedisStore store,
        ILeaderGate? leaderGate,
        ILogger logger,
        TimeSpan? interval = null)
    {
        _executor = executor;
        _loopService = loopService;
        _configClient = configClient;
        _store = store;
        _leaderGate = leaderGate;
        _logger = logger.ForContext<LoopCommandHandlerService>();
        _interval = interval ?? TimeSpan.FromSeconds(1);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("LoopCommandHandlerService started — polling every {Interval}", _interval);

        // Immediate first poll (same as WorkItemCountsService)
        await HandlePendingCommandAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await HandlePendingCommandAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Reads and handles a pending loop command from Redis if this pod is the leader
    /// and the command has not already been handled.
    /// </summary>
    internal async Task HandlePendingCommandAsync(CancellationToken ct)
    {
        // Only the leader handles commands.
        if (_leaderGate is { IsLeader: false }) return;

        try
        {
            var json = await _store.GetAsync(LoopCommandRelay.CommandKey);
            if (json is null) return;

            var message = JsonSerializer.Deserialize<LoopCommandMessage>(json, PipelineJsonOptions.Lenient);
            if (message is null) return;

            // Idempotency guard — skip if already handled.
            if (message.Id == _lastHandledId) return;

            _logger.Information(
                "LoopCommandHandlerService: handling command {Command} (id={Id})",
                message.Command, message.Id);

            LoopCommandResultMessage result;
            try
            {
                result = await ExecuteCommandAsync(message.Command, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex,
                    "LoopCommandHandlerService: command {Command} (id={Id}) failed",
                    message.Command, message.Id);
                // NOTE (issue #3552): ex.Message is written verbatim into Redis and returned to the
                // caller's UI. If the underlying library includes connection strings, hostnames
                // or credentials in its exception message, those details would be stored in Redis
                // and surfaced to the caller. Consider replacing this with a generic message such
                // as "Command failed — see Scheduler logs for details." (see Security review).
                result = new LoopCommandResultMessage(false, null,
                    $"Command failed: {ex.Message}");
            }

            var resultJson = JsonSerializer.Serialize(result, PipelineJsonOptions.Default);
            var resultKey = LoopCommandRelay.ResultKeyPrefix + message.Id;
            await _store.SetAsync(resultKey, resultJson, TimeSpan.FromSeconds(60));

            _lastHandledId = message.Id;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host stopping — exit silently.
            // NOTE (issue #3552): When cancelled, _lastHandledId is not updated and the result key is
            // not written to Redis. If a new leader starts before the relay's 10 s timeout elapses,
            // it will see the same scheduler:loop-command key (still live within its 60 s TTL),
            // find its own _lastHandledId fresh, and re-execute the command. For Start this is
            // harmless (StartLoopAsync is idempotent), but the re-execution is undocumented and
            // differs from the in-process idempotency guarantee (see Correctness review).
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "LoopCommandHandlerService: error polling for loop command — will retry next interval");
        }
    }

    private async Task<LoopCommandResultMessage> ExecuteCommandAsync(LoopCommand command, CancellationToken ct)
    {
        switch (command)
        {
            case LoopCommand.Start:
                {
                    var dto = await _executor.ExecuteStartAsync(_loopService, _configClient, ct);
                    // NOTE (issue #3552): When dto.Started is false, Success=false and Error=null here.
                    // SchedulerLoopEndpoints.StartLoop's relay fall-through branch reads result.Error
                    // (null) and returns { Started: false, Error: null } to the caller, silently
                    // dropping the leader's failure reason (e.g. "Loop is already active.").
                    // Fix: populate Error from dto.Error when dto.Started is false, e.g.:
                    //   return new LoopCommandResultMessage(dto.Started, dto, dto.Started ? null : dto.Error);
                    // (see Correctness review and SUGGESTION in review-findings.md).
                    return new LoopCommandResultMessage(dto.Started, dto, null);
                }
            case LoopCommand.Stop:
                {
                    // Config persistence was already done locally by the non-leader endpoint.
                    // Here we only call StopLoop() on the leader.
                    await _executor.ExecuteStopLoopOnlyAsync(_loopService);
                    return new LoopCommandResultMessage(true, null, null);
                }
            case LoopCommand.Resume:
                {
                    await _executor.ExecuteResumeAsync(_loopService);
                    return new LoopCommandResultMessage(true, null, null);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command,
                    "Unknown loop command.");
        }
    }
}
