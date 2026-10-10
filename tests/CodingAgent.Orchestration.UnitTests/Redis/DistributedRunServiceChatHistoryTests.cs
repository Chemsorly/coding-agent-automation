using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Serilog;

namespace CodingAgent.Orchestration.UnitTests.Redis;

/// <summary>
/// Repro test for issue #3502: chat entries reported through AgentHub.ReportChatEntry
/// must survive the Redis round-trip in DistributedRunService.
/// </summary>
public sealed class DistributedRunServiceChatHistoryTests
{
    [Theory]
    [InlineData("in-memory")]
    [InlineData("redis")]
    public async Task Repro_DistributedRunService_ChatEntryReportedThroughAgentHubIsReturnedByRemoveRun(string mode)
    {
        IOrchestratorRunService runService = mode == "redis"
            ? new DistributedRunService(new FakeRedisStore(), (_, _, _) => Task.FromResult(false), Log.Logger)
            : new OrchestratorRunService(Log.Logger);

        var facade = new AgentHubFacade(new AgentHubFacadeDependencies(
            Mock.Of<IAgentRegistryService>(),
            runService,
            Mock.Of<IPipelineRunHistoryService>(),
            Mock.Of<IProviderConfigStore>(),
            Mock.Of<IProviderFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentHubFacadeDependencies>.Instance));

        var hub = new AgentHub(new AgentHubDependencies(
            facade,
            Mock.Of<IChatNotifier>(),
            Mock.Of<IChangeNotifier>(),
            Mock.Of<IHubConsolidationOperations>(),
            Mock.Of<IHubIssueOperations>(),
            Mock.Of<IAgentJobLifecycleService>(),
            Mock.Of<IAgentTokenRefreshService>(),
            Mock.Of<Serilog.ILogger>(),
            Mock.Of<IAgentOrphanRecoveryService>(),
            CreateNoOpHubContext()));

        var jobId = Guid.NewGuid().ToString();
        runService.AddRun(new PipelineRun
        {
            RunId = jobId,
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
        });

        // Production write path for chat history (agent -> hub -> facade -> run service).
        await hub.ReportChatEntry(jobId, ChatRole.Agent, "analysis complete");

        // TODO: Also assert GetChatHistoryAsync returns the entry before RemoveRun is called.
        // This would make the repro test self-contained as a regression guard for the full reported
        // failure mode (SubscribeToRun snapshot seeding), not just the RemoveRun path.

        // RunLifecycleManager claims the finished run through RemoveRun.
        var removed = runService.RemoveRun(new RunId(jobId));

        removed.Should().NotBeNull();
        removed!.ChatHistory.Select(e => e.Content).Should().ContainSingle(
            "a chat entry reported during the run must be part of the run RemoveRun returns ({0})", mode)
            .Which.Should().Be("analysis complete");
    }

    private static IHubContext<AgentHub> CreateNoOpHubContext()
    {
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        var context = new Mock<IHubContext<AgentHub>>();
        context.Setup(h => h.Clients).Returns(clients.Object);
        return context.Object;
    }
}
