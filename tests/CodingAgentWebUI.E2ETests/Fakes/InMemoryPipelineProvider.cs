using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Fakes;

/// <summary>
/// In-memory pipeline provider for E2E tests. Simulates external CI pass/fail.
/// </summary>
public sealed class InMemoryPipelineProvider : IPipelineProvider
{
    public PipelineProviderType ProviderType => PipelineProviderType.GitHubActions;
    public bool ShouldPass { get; set; } = true;
    public TimeSpan SimulatedDelay { get; set; } = TimeSpan.Zero;

    public void Reset()
    {
        ShouldPass = true;
        SimulatedDelay = TimeSpan.Zero;
    }

    public async Task<PipelineRunStatus> GetRunStatusAsync(string branchName, string? commitSha, CancellationToken ct)
    {
        if (SimulatedDelay > TimeSpan.Zero)
            await Task.Delay(SimulatedDelay, ct);

        return new PipelineRunStatus
        {
            State = ShouldPass ? PipelineRunState.Passed : PipelineRunState.Failed,
            Jobs = new[]
            {
                new PipelineJobResult
                {
                    Name = "build-and-test",
                    State = ShouldPass ? PipelineRunState.Passed : PipelineRunState.Failed,
                    FailureReason = ShouldPass ? null : "Tests failed"
                }
            }
        };
    }

    public async Task<PipelineRunStatus> WaitForCompletionAsync(string branchName, string? commitSha, TimeSpan timeout, CancellationToken ct)
    {
        if (SimulatedDelay > TimeSpan.Zero)
            await Task.Delay(SimulatedDelay, ct);

        return await GetRunStatusAsync(branchName, commitSha, ct);
    }

    public Task<string?> GetJobLogsAsync(long jobId, CancellationToken ct) =>
        Task.FromResult<string?>("Fake CI log output");

    public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
