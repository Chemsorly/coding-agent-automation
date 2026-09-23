using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="AgentIdleTransitioner.TransitionToIdle"/>.
/// Covers both the agent-not-null path and the agent-null fallback path.
/// </summary>
public sealed class AgentIdleTransitionerTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly AgentIdleTransitioner _sut;

    public AgentIdleTransitionerTests()
    {
        _sut = new AgentIdleTransitioner(_facade.Object, _logger.Object);
    }

    private static AgentEntry MakeAgent(string agentId = "agent-1") =>
        new()
        {
            AgentId = new AgentId(agentId),
            ConnectionId = $"conn-{agentId}",
            Hostname = "test-host",
            Labels = [],
            RegisteredAt = DateTimeOffset.UtcNow,
            Status = AgentStatus.Idle
        };

    private static PipelineRun MakeRun(string agentId = "agent-1") => new()
    {
        RunId = "job-1",
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "github",
        RepoProviderConfigId = "github-repo",
        AgentId = agentId
    };

    // ── Agent not null path ────────────────────────────────────────────────

    [Fact]
    public void TransitionToIdle_AgentNotNull_ClearsActiveJobId()
    {
        var agent = MakeAgent();
        agent.ActiveJobId = "job-1";

        _sut.TransitionToIdle(agent, run: null, jobIdValue: "job-1");

        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public void TransitionToIdle_AgentNotNull_ClearsOrphanRestoredAt()
    {
        var agent = MakeAgent();
        agent.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        _sut.TransitionToIdle(agent, run: null, jobIdValue: "job-1");

        agent.OrphanRestoredAt.Should().BeNull();
    }

    [Fact]
    public void TransitionToIdle_AgentNotNull_SetsLastJobCompletedAt()
    {
        var agent = MakeAgent();
        agent.LastJobCompletedAt = null;
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        _sut.TransitionToIdle(agent, run: null, jobIdValue: "job-1");

        agent.LastJobCompletedAt.Should().NotBeNull();
        agent.LastJobCompletedAt!.Value.Should().BeAfter(before);
        // TODO: [WARNING] This test only verifies the in-memory field. There is no corresponding
        // assertion that the Redis field (lastJobCompletedAt via UpdateAgentFieldAsync) receives a
        // value that matches or is consistent with agent.LastJobCompletedAt. The production code
        // currently captures DateTimeOffset.UtcNow twice (acknowledged TODO in AgentIdleTransitioner),
        // meaning the two values can diverge. A complementary assertion on the UpdateAgentFieldAsync
        // call (e.g. It.Is<string>(v => DateTimeOffset.TryParse(v, out var t) && t >= before))
        // would pin this behavior and catch a future regression.
        // (TestQualityReviewer review finding.)
    }

    [Fact]
    public void TransitionToIdle_AgentNotNull_CallsTransitionStatusIdle()
    {
        var agent = MakeAgent();

        _sut.TransitionToIdle(agent, run: null, jobIdValue: "job-1");

        _facade.Verify(f => f.TransitionStatus(agent.AgentId, AgentStatus.Idle), Times.Once);
    }

    [Fact]
    public void TransitionToIdle_AgentNotNull_FiresThreeFieldUpdates()
    {
        var agent = MakeAgent();

        _sut.TransitionToIdle(agent, run: null, jobIdValue: "job-1");

        // UpdateAgentFieldFireAndForget is an extension method that delegates to UpdateAgentFieldAsync;
        // verify the underlying interface method (Moq cannot verify extension methods directly).
        _facade.Verify(f => f.UpdateAgentFieldAsync(agent.AgentId, "activeJobId", null), Times.Once);
        _facade.Verify(f => f.UpdateAgentFieldAsync(agent.AgentId, "orphanRestoredAt", null), Times.Once);
        // TODO: [WARNING] It.IsAny<string?>() does not verify the value format. The production code
        // writes DateTimeOffset.UtcNow.ToString("O") (ISO 8601 round-trip). This assertion would pass
        // even if the value were null, empty, or an entirely wrong format. Strengthen by asserting the
        // value is a non-null, non-empty string parseable as DateTimeOffset (e.g. using It.Is<string>).
        // (TestQualityReviewer review finding.)
        _facade.Verify(f => f.UpdateAgentFieldAsync(agent.AgentId, "lastJobCompletedAt", It.IsAny<string?>()), Times.Once);
    }

    // ── Agent null, run has AgentId (fallback path) ────────────────────────

    [Fact]
    public void TransitionToIdle_AgentNull_RunHasAgentId_CallsTransitionStatusIdle()
    {
        var run = MakeRun("agent-42");
        var expectedAgentId = new AgentId("agent-42");

        _sut.TransitionToIdle(agent: null, run, jobIdValue: "job-1");

        _facade.Verify(f => f.TransitionStatus(expectedAgentId, AgentStatus.Idle), Times.Once);
    }

    [Fact]
    public void TransitionToIdle_AgentNull_RunHasAgentId_ClearsActiveJobIdField()
    {
        var run = MakeRun("agent-42");
        var expectedAgentId = new AgentId("agent-42");

        _sut.TransitionToIdle(agent: null, run, jobIdValue: "job-1");

        // Verify the underlying UpdateAgentFieldAsync (extension method not mockable directly).
        _facade.Verify(f => f.UpdateAgentFieldAsync(expectedAgentId, "activeJobId", null), Times.Once);
        // TODO: [WARNING] This test does not assert that orphanRestoredAt is NOT cleared on the
        // fallback path. The fallback path intentionally omits the orphanRestoredAt clear (unlike the
        // agent-not-null path). Without a Times.Never assertion here, a future change that accidentally
        // adds the extra field clear on the fallback path would go undetected.
        // (TestQualityReviewer review finding.)
    }

    [Fact]
    public void TransitionToIdle_AgentNull_RunHasAgentId_LogsWarning()
    {
        var run = MakeRun("agent-42");

        _sut.TransitionToIdle(agent: null, run, jobIdValue: "job-1");

        _logger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("agent lookup returned null") && s.Contains("{JobId}") && s.Contains("{AgentId}")),
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Once);
    }

    // ── No-op paths ────────────────────────────────────────────────────────

    [Fact]
    public void TransitionToIdle_AgentNull_RunIsNull_NoOp()
    {
        _sut.TransitionToIdle(agent: null, run: null, jobIdValue: "job-1");

        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
        // Verify underlying interface method (extension method not mockable).
        _facade.Verify(f => f.UpdateAgentFieldAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public void TransitionToIdle_AgentNull_RunAgentIdIsNull_NoOp()
    {
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = null   // no agent assigned to this run
        };

        _sut.TransitionToIdle(agent: null, run, jobIdValue: "job-1");

        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }
}
