using AwesomeAssertions;
using CodingAgent.Agent.Executors;
using CodingAgent.Contracts;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using MessagePack;

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

    [Fact]
    public void CopyUsageTo_SetsPhaseBreakdownFromRunPhases()
    {
        // Arrange — accumulate usage into a named phase
        var job = CreateJob();
        var progress = new ConsolidationProgress(ConsolidationProgress.CreateRun(job), _ => { });

        var agentResult = new AgentResult
        {
            ExitCode = 0,
            OutputLines = [],
            Usage = new TokenUsage { InputTokens = 250 },
            Cost = 0.01m
        };
        progress.AccumulateTokenUsage(agentResult, "refactoring_structural");
        progress.AccumulateTokenUsage(agentResult, "refactoring_test_gaps");

        var jobResult = new ConsolidationJobResult { JobId = "j", Success = true };

        // Act
        progress.CopyUsageTo(jobResult);

        // Assert
        jobResult.PhaseBreakdown.Should().NotBeNull("CopyUsageTo must set PhaseBreakdown from the run's phase data");
        jobResult.PhaseBreakdown!.Should().ContainKey("refactoring_structural");
        jobResult.PhaseBreakdown!.Should().ContainKey("refactoring_test_gaps");
        jobResult.PhaseBreakdown["refactoring_structural"].Tokens.Should().Be(250);
    }

    [Fact]
    public void ConsolidationJobResult_MessagePackRoundTrip_PhaseBreakdownIsIgnoredOnWire_TotalTokensPreserved()
    {
        // Arrange — a result with both [Key] fields and [IgnoreMember] PhaseBreakdown set
        var original = new ConsolidationJobResult
        {
            JobId = "job-mp-test",
            Success = true,
            TotalTokens = 12345,
            PhaseBreakdown = new Dictionary<string, PhaseUsagePayload>
            {
                ["analysis"] = new() { Tokens = 12345, Provider = "kiro" }
            }
        };

        // Act — serialize with the standard hub options then deserialize
        var bytes = MessagePackSerializer.Serialize(original, AgentHubMessagePack.SerializerOptions);
        var deserialized = MessagePackSerializer.Deserialize<ConsolidationJobResult>(bytes, AgentHubMessagePack.SerializerOptions);

        // Assert — [IgnoreMember] means PhaseBreakdown is NOT on the wire; TotalTokens is preserved
        deserialized.PhaseBreakdown.Should().BeNull(
            "[IgnoreMember] means PhaseBreakdown must not be serialized into the hub message");
        deserialized.TotalTokens.Should().Be(12345, "TotalTokens is a [Key] field and must survive the round-trip");
        deserialized.JobId.Should().Be("job-mp-test");
        deserialized.Success.Should().BeTrue();
    }
}
