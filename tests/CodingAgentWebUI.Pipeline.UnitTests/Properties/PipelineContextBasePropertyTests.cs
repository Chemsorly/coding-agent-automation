using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Feature: 018-encapsulation-improvements, Property 5: PipelineContextBase inheritance preserves access
/// 
/// For any AgentPhaseContext instance, accessing the 6 base properties (Run, Config, AgentProvider,
/// IssueOps, Callbacks, OrchestratorCts) returns the same values whether accessed via the concrete
/// type or cast to PipelineContextBase.
/// 
/// **Validates: Requirements 36.4, 36.5**
/// </summary>
public class PipelineContextBasePropertyTests
{
    /// <summary>
    /// Property 5: PipelineContextBase inheritance preserves access.
    /// For any AgentPhaseContext instance, base properties accessible via concrete type and via
    /// cast to PipelineContextBase return same references.
    /// **Validates: Requirements 36.4, 36.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public void AgentPhaseContext_BaseProperties_AccessibleViaCastReturnSameReferences(
        bool includeOrchestratorCts)
    {
        // Arrange: create unique mock instances for each interface property
        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        var config = TestPipelineConfig.Default();
        var agentProvider = new Mock<IAgentProvider>().Object;
        var issueOps = new Mock<IAgentIssueOperations>().Object;
        var callbacks = new Mock<IPipelineCallbacks>().Object;
        var orchestratorCts = includeOrchestratorCts ? new CancellationTokenSource() : null;

        var issue = new IssueDetail
        {
            Identifier = "42",
            Title = "Test Issue",
            Description = "Description",
            Labels = Array.Empty<string>()
        };
        var parsedIssue = new ParsedIssue
        {
            RequirementsSection = "Requirements",
            AcceptanceCriteria = new[] { "AC1" }
        };

        var context = new AgentPhaseContext
        {
            Run = run,
            Config = config,
            AgentProvider = agentProvider,
            IssueOps = issueOps,
            Callbacks = callbacks,
            OrchestratorCts = orchestratorCts,
            Issue = issue,
            ParsedIssue = parsedIssue
        };

        // Act: cast to base type
        PipelineContextBase baseContext = context;

        // Assert: all 6 base properties return the same reference via both access paths
        baseContext.Run.Should().BeSameAs(context.Run);
        baseContext.Config.Should().BeSameAs(context.Config);
        baseContext.AgentProvider.Should().BeSameAs(context.AgentProvider);
        baseContext.IssueOps.Should().BeSameAs(context.IssueOps);
        baseContext.Callbacks.Should().BeSameAs(context.Callbacks);
        baseContext.OrchestratorCts.Should().BeSameAs(context.OrchestratorCts);

        // Cleanup
        orchestratorCts?.Dispose();
    }

    /// <summary>
    /// Property 5 (QualityGateContext variant): PipelineContextBase inheritance preserves access.
    /// For any QualityGateContext instance, base properties accessible via concrete type and via
    /// cast to PipelineContextBase return same references.
    /// **Validates: Requirements 36.4, 36.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public void QualityGateContext_BaseProperties_AccessibleViaCastReturnSameReferences(
        bool includeOrchestratorCts)
    {
        // Arrange: create unique mock instances for each interface property
        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "2",
            IssueTitle = "QG Test",
            IssueProviderConfigId = "ip2",
            RepoProviderConfigId = "rp2",
            StartedAt = DateTime.UtcNow
        };
        var config = TestPipelineConfig.Default();
        var agentProvider = new Mock<IAgentProvider>().Object;
        var issueOps = new Mock<IAgentIssueOperations>().Object;
        var callbacks = new Mock<IPipelineCallbacks>().Object;
        var orchestratorCts = includeOrchestratorCts ? new CancellationTokenSource() : null;
        var repoProvider = new Mock<IRepositoryProvider>().Object;

        var context = new QualityGateContext
        {
            Run = run,
            Config = config,
            AgentProvider = agentProvider,
            IssueOps = issueOps,
            Callbacks = callbacks,
            OrchestratorCts = orchestratorCts,
            RepoProvider = repoProvider
        };

        // Act: cast to base type
        PipelineContextBase baseContext = context;

        // Assert: all 6 base properties return the same reference via both access paths
        baseContext.Run.Should().BeSameAs(context.Run);
        baseContext.Config.Should().BeSameAs(context.Config);
        baseContext.AgentProvider.Should().BeSameAs(context.AgentProvider);
        baseContext.IssueOps.Should().BeSameAs(context.IssueOps);
        baseContext.Callbacks.Should().BeSameAs(context.Callbacks);
        baseContext.OrchestratorCts.Should().BeSameAs(context.OrchestratorCts);

        // Cleanup
        orchestratorCts?.Dispose();
    }

    /// <summary>
    /// Property 5 (multiple instances): For multiple AgentPhaseContext instances with different
    /// values, each instance's base properties are independently correct when accessed via cast.
    /// **Validates: Requirements 36.4, 36.5**
    /// </summary>
    [Property(MaxTest = 20)]
    public void MultipleContextInstances_EachPreservesOwnBaseProperties(PositiveInt countRaw)
    {
        var count = Math.Min(countRaw.Get, 10); // Cap at 10 for performance

        var contexts = new List<AgentPhaseContext>();
        for (var i = 0; i < count; i++)
        {
            var run = new PipelineRun
            {
                RunId = $"run-{i}",
                IssueIdentifier = i.ToString(),
                IssueTitle = $"Issue {i}",
                IssueProviderConfigId = $"ip-{i}",
                RepoProviderConfigId = $"rp-{i}",
                StartedAt = DateTime.UtcNow
            };

            contexts.Add(new AgentPhaseContext
            {
                Run = run,
                Config = TestPipelineConfig.Default(),
                AgentProvider = new Mock<IAgentProvider>().Object,
                IssueOps = new Mock<IAgentIssueOperations>().Object,
                Callbacks = new Mock<IPipelineCallbacks>().Object,
                OrchestratorCts = null,
                Issue = new IssueDetail
                {
                    Identifier = i.ToString(),
                    Title = $"Issue {i}",
                    Description = $"Desc {i}",
                    Labels = Array.Empty<string>()
                },
                ParsedIssue = new ParsedIssue
                {
                    RequirementsSection = $"Req {i}",
                    AcceptanceCriteria = new[] { $"AC {i}" }
                }
            });
        }

        // Assert: each context's base properties are correct when accessed via cast
        foreach (var context in contexts)
        {
            PipelineContextBase baseContext = context;
            baseContext.Run.Should().BeSameAs(context.Run);
            baseContext.Config.Should().BeSameAs(context.Config);
            baseContext.AgentProvider.Should().BeSameAs(context.AgentProvider);
            baseContext.IssueOps.Should().BeSameAs(context.IssueOps);
            baseContext.Callbacks.Should().BeSameAs(context.Callbacks);
            baseContext.OrchestratorCts.Should().BeSameAs(context.OrchestratorCts);
        }
    }
}
