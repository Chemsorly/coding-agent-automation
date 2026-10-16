using AwesomeAssertions;
using CodingAgent.Agent.Executors;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.UnitTests.Executors;

/// <summary>
/// Unit tests for <see cref="ConsolidationProgress"/> token-usage methods.
/// </summary>
public class ConsolidationProgressTests
{
    private static ConsolidationJobMessage CreateJob()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cp-test-{Guid.NewGuid()}");
        return new ConsolidationJobMessage
        {
            JobId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            TemplateId = "template-1",
            TemplateName = "Test Template",
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration { WorkspaceBaseDirectory = tempDir }
        };
    }

    [Fact]
    public async Task AccumulateTokenUsage_FromParallelCalls_CountsEveryCall()
    {
        // Arrange
        var job = CreateJob();
        var progress = new ConsolidationProgress(ConsolidationProgress.CreateRun(job), _ => { });

        var agentResult = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 1 }
        };

        // Act — three tasks each call AccumulateTokenUsage 1000 times concurrently
        var tasks = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
                progress.AccumulateTokenUsage(agentResult, "p");
        })).ToArray();

        await Task.WhenAll(tasks);

        // Assert
        progress.Run.TotalTokens.Should().Be(3000,
            "three tasks × 1000 calls × 1 InputToken = 3000; the lock must prevent double-counting");
    }

    [Fact]
    public void CopyUsageTo_CopiesTotals()
    {
        // Arrange
        var job = CreateJob();
        var progress = new ConsolidationProgress(ConsolidationProgress.CreateRun(job), _ => { });

        var agentResult = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 100, CacheReadTokens = 20 },
            Cost = 0.5m
        };

        progress.AccumulateTokenUsage(agentResult, "test_phase");

        var jobResult = new ConsolidationJobResult { JobId = "j", Success = true };

        // Act
        progress.CopyUsageTo(jobResult);

        // Assert
        jobResult.TotalTokens.Should().Be(100, "InputTokens = 100, no output or reasoning");
        jobResult.TotalCost.Should().Be(0.5m, "Cost = 0.5m");
        jobResult.CacheReadTokens.Should().Be(20, "CacheReadTokens = 20");
    }
}
