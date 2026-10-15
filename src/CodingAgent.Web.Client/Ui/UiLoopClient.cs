using CodingAgent.Contracts.Ui;

namespace CodingAgent.Web.Ui;

/// <summary>
/// The pipeline loop (spec 051 D11). Start, Stop and Resume only record the request; the Scheduler leader
/// applies it within a few seconds, and the status shows it: <see cref="UiLoopStatus.RunRequested"/>
/// changes at once, <see cref="UiLoopStatus.IsLoopActive"/> once the loop follows. The status is open to
/// every user (filtered for project-scoped ones); the commands need global operator.
/// </summary>
public interface IUiLoopClient
{
    Task<UiLoopStatus> GetStatusAsync(CancellationToken ct = default);

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    Task ResumeAsync(CancellationToken ct = default);
}

public sealed class UiLoopClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiLoopClient
{
    private const string Loop = "/api/ui/loop";

    public Task<UiLoopStatus> GetStatusAsync(CancellationToken ct = default) =>
        GetAsync($"{Loop}/status", UiJsonContext.Default.UiLoopStatus, ct);

    public Task StartAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"{Loop}/start", ct);

    public Task StopAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"{Loop}/stop", ct);

    public Task ResumeAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"{Loop}/resume", ct);
}
