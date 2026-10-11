using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Fakes;

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

    public async Task<PipelineRunStatus> GetRunStatusAsync(BranchName branchName, string? commitSha, CancellationToken ct)
    {
        // TODO [WARNING] (TestQualityReviewer): branchName.Value is never read in this fake, so an
        // E2E test that inadvertently passes the wrong branch name (e.g. transposed with commitSha
        // via the implicit string→BranchName conversion) would not be detected. If branch-name
        // correctness needs to be validated in E2E tests, consider exposing a
        // `LastBranchName` property or adding an assertion on branchName.Value.
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

    public async Task<PipelineRunStatus> WaitForCompletionAsync(BranchName branchName, string? commitSha, TimeSpan timeout, CancellationToken ct)
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
