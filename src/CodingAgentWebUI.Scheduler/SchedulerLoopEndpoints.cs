using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Scheduler;

/// <summary>
/// Minimal API endpoints for loop control, hosted by the Scheduler on port 8091.
/// Called by the WebUI to start/stop/resume the loop and poll status.
///
/// Authentication: X-Api-Key header must match AGENT_API_KEY env var — same key as the API.
/// </summary>
public static class SchedulerLoopEndpoints
{
    /// <summary>
    /// Maps loop control endpoints and wires the OnChange cache update.
    /// Must be called after PipelineLoopService and LoopStatusCache are registered in DI.
    /// </summary>
    public static void MapSchedulerLoopEndpoints(this IEndpointRouteBuilder app)
    {
        var apiKey = app.ServiceProvider.GetRequiredService<IConfiguration>()
            .GetValue<string>("AGENT_API_KEY")
            ?? app.ServiceProvider.GetRequiredService<IConfiguration>()
                .GetValue<string>("AgentApiKey");

        // Wire cache update on loop state change — via the DI-scoped LoopStatusCache singleton
        var cache = app.ServiceProvider.GetRequiredService<LoopStatusCache>();
        var loopService = app.ServiceProvider.GetRequiredService<IPipelineLoopService>();
        loopService.OnChange += () => cache.Update(BuildDto(loopService));

        // Emit a warning when the API key filter will be in fail-open mode
        if (string.IsNullOrEmpty(apiKey))
            Serilog.Log.Warning("SchedulerLoopEndpoints: AGENT_API_KEY is not configured — loop endpoints are unauthenticated");

        var group = app.MapGroup("/loop")
            .AddEndpointFilter(new ApiKeyFilter(apiKey ?? ""));

        group.MapGet("/status", GetLoopStatus);
        group.MapPost("/start", StartLoop);
        group.MapPost("/stop", StopLoop);
        group.MapPost("/resume", ResumeLoop);
    }

    // ── Handlers ─────────────────────────────────────────────────────────────

    internal static IResult GetLoopStatus(IPipelineLoopService loopService, LoopStatusCache cache)
    {
        // Serve from the DI-singleton cache — avoids lock contention on the loop service.
        // Falls back to building on demand when the cache has not been populated yet.
        var dto = cache.Read() ?? BuildDto(loopService);
        return Results.Ok(dto);
    }

    internal static async Task<IResult> StartLoop(
        IPipelineLoopService loopService,
        IPipelineApiConfigClient configClient,
        CancellationToken ct)
    {
        var started = await loopService.StartLoopAsync();
        if (started)
        {
            // Persist ClosedLoopAutoStart=true so the Scheduler auto-starts on next boot
            await configClient.UpdatePipelineConfigAsync(c => c with { ClosedLoopAutoStart = true }, ct);
        }
        var error = started ? null
            : loopService.ValidationErrors.Count > 0 ? "Loop failed to start due to validation errors."
            : loopService.IsLoopActive ? "Loop is already active."
            : "A manual run is in progress. Wait for it to complete.";
        return Results.Ok(new LoopStartResultDto(started, error));
    }

    internal static async Task<IResult> StopLoop(
        IPipelineLoopService loopService,
        IPipelineApiConfigClient configClient,
        CancellationToken ct)
    {
        loopService.StopLoop();
        await configClient.UpdatePipelineConfigAsync(c => c with { ClosedLoopAutoStart = false }, ct);
        return Results.NoContent();
    }

    internal static IResult ResumeLoop(IPipelineLoopService loopService)
    {
        loopService.ResumeLoop();
        return Results.NoContent();
    }

    internal static LoopStatusDto BuildDto(IPipelineLoopService svc) => new(
        svc.IsLoopActive,
        svc.StatusMessage,
        svc.CurrentIssueIdentifier,
        svc.ProcessedCount,
        svc.FailedCount,
        svc.QueueCount,
        svc.IsCircuitBroken,
        svc.LastPollError,
        svc.CurrentCycleTemplateIndex,
        svc.CurrentCycleTemplateCount,
        svc.ValidationErrors,
        svc.TemplateStatuses);

    // ── API key filter ────────────────────────────────────────────────────────

    private sealed class ApiKeyFilter : IEndpointFilter
    {
        private readonly string _expectedKey;
        public ApiKeyFilter(string expectedKey) => _expectedKey = expectedKey;

        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
        {
            if (string.IsNullOrEmpty(_expectedKey))
            {
                // No key configured — fail closed to prevent unauthenticated access in production.
                // If AGENT_API_KEY is missing (misconfiguration or partial local config), returning
                // 503 is safer than allowing unrestricted loop control to anyone on the network.
                return Results.Problem(
                    title: "Service Unavailable",
                    detail: "AGENT_API_KEY is not configured. Loop control endpoints are disabled.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!ctx.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var provided)
                || provided != _expectedKey)
            {
                return Results.Unauthorized();
            }
            return await next(ctx);
        }
    }
}

/// <summary>
/// DI-singleton that holds the most-recently-built <see cref="LoopStatusDto"/> snapshot.
/// Updated via <see cref="PipelineLoopService.OnChange"/>; served by the /loop/status handler.
///
/// Using a registered singleton rather than a static field ensures state is scoped to a
/// single DI container — preventing test isolation issues when multiple
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/> instances
/// run in the same process.
/// </summary>
public sealed class LoopStatusCache
{
    private LoopStatusDto? _value;

    /// <summary>Stores a new snapshot. Thread-safe via reference replacement.</summary>
    public void Update(LoopStatusDto dto) => Volatile.Write(ref _value, dto);

    /// <summary>Returns the current snapshot, or null if not yet populated.</summary>
    public LoopStatusDto? Read() => Volatile.Read(ref _value);
}
