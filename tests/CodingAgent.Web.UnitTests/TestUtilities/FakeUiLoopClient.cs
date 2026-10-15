using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;

namespace CodingAgent.Web.UnitTests.TestUtilities;

/// <summary>
/// Spec 051 D11: an <see cref="IUiLoopClient"/> for page tests. It serves <see cref="Status"/> and records
/// the commands; Start and Stop change <see cref="UiLoopStatus.RunRequested"/> at once, as the API does,
/// and leave <see cref="UiLoopStatus.IsLoopActive"/> to the test (the Scheduler's part).
/// </summary>
public sealed class FakeUiLoopClient : IUiLoopClient
{
    /// <summary>A stopped loop the Scheduler has reported.</summary>
    public static UiLoopStatus Stopped { get; } = new(
        false, false, "", null, 0, 0, 0, false, null, 0, 0,
        [], new Dictionary<string, ConfigStatusSnapshot>(), DateTimeOffset.UnixEpoch);

    /// <summary>A loop the user started and the Scheduler runs.</summary>
    public static UiLoopStatus Running { get; } = Stopped with { IsLoopActive = true, RunRequested = true, CurrentCycleTemplateCount = 1 };

    public UiLoopStatus Status { get; set; } = Stopped;

    /// <summary>The commands sent, in order: "start", "stop" or "resume".</summary>
    public List<string> Commands { get; } = [];

    /// <summary>When set, every command fails with it.</summary>
    public Exception? CommandFailure { get; set; }

    public Task<UiLoopStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Status);

    public Task StartAsync(CancellationToken ct = default) => SendAsync("start", s => s with { RunRequested = true });

    public Task StopAsync(CancellationToken ct = default) => SendAsync("stop", s => s with { RunRequested = false });

    public Task ResumeAsync(CancellationToken ct = default) => SendAsync("resume", s => s);

    private Task SendAsync(string command, Func<UiLoopStatus, UiLoopStatus> apply)
    {
        Commands.Add(command);
        if (CommandFailure is not null)
            return Task.FromException(CommandFailure);
        Status = apply(Status);
        return Task.CompletedTask;
    }
}
