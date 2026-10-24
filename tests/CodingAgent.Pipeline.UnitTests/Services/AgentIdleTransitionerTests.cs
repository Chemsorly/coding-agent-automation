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

        _sut.TransitionToIdle(agent, run: null, jobIdValue: "job-1", callerContext: "test");

        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public void TransitionToIdle_AgentNotNull_ClearsOrphanRestoredAt()
    {
        var agent = MakeAgent();
        agent.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        _sut.TransitionToIdle(agent, run: null, jobIdValue: "job-1", callerContext: "test");

        agent.OrphanRestoredAt.Should().BeNull();
    }

    [Fact]
    public void TransitionToIdle_AgentNotNull_SetsLastJobCompletedAt()
    {
        var agent = MakeAgent();
        agent.LastJobCompletedAt = null;
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        _sut.TransitionToIdle(agent, run: null, jobIdValue: "job-1", callerContext: "test");

        agent.LastJobCompletedAt.Should().NotBeNull();
        agent.LastJobCompletedAt!.Value.Should().BeAfter(before);
    }

    [Fact]
    public void TransitionToIdle_AgentNotNull_CallsTransitionStatusIdle()
    {
        var agent = MakeAgent();

        _sut.TransitionToIdle(agent, run: null, jobIdValue: "job-1", callerContext: "test");

        _facade.Verify(f => f.TransitionStatus(agent.AgentId, AgentStatus.Idle), Times.Once);
    }

    [Fact]
    public void TransitionToIdle_AgentNotNull_FiresThreeFieldUpdates()
    {
        var agent = MakeAgent();

        _sut.TransitionToIdle(agent, run: null, jobIdValue: "job-1", callerContext: "test");

        // UpdateAgentFieldFireAndForget is an extension method that delegates to UpdateAgentFieldAsync;
        // verify the underlying interface method (Moq cannot verify extension methods directly).
        _facade.Verify(f => f.UpdateAgentFieldAsync(agent.AgentId, "activeJobId", null), Times.Once);
        _facade.Verify(f => f.UpdateAgentFieldAsync(agent.AgentId, "orphanRestoredAt", null), Times.Once);
        _facade.Verify(f => f.UpdateAgentFieldAsync(
            agent.AgentId,
            "lastJobCompletedAt",
            It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public void TransitionToIdle_AgentNotNull_InMemoryLastJobCompletedAt_MatchesRedisValue()
    {
        // Verifies that the single-clock-read fix is in effect: the in-memory field and the Redis
        // write use the identical DateTimeOffset value (captured once via `var now = DateTimeOffset.UtcNow`).
        // TODO: [WARNING] This test does not reliably catch a double-read regression: two consecutive
        // `DateTimeOffset.UtcNow` calls on the same thread in a tight unit test almost always return
        // the same value (within OS timer resolution), so reverting the single-clock fix would leave
        // the assertion green. Making the guard reliable requires an injectable `ISystemClock`
        // abstraction in AgentIdleTransitioner that can return distinct timestamps in tests.
        // (TestQualityReviewer finding.)
        var agent = MakeAgent();
        string? capturedRedisValue = null;
        _facade
            .Setup(f => f.UpdateAgentFieldAsync(agent.AgentId, "lastJobCompletedAt", It.IsAny<string?>()))
            .Callback<AgentId, string, string?>((_, _, v) => capturedRedisValue = v)
            .Returns(Task.CompletedTask);

        _sut.TransitionToIdle(agent, run: null, jobIdValue: "", callerContext: "test");

        // UpdateAgentFieldFireAndForget fires synchronously before ContinueWith is attached,
        // so capturedRedisValue is set before TransitionToIdle returns.
        capturedRedisValue.Should().NotBeNullOrEmpty();
        DateTimeOffset.TryParse(capturedRedisValue, out var redisTime).Should().BeTrue();
        agent.LastJobCompletedAt.Should().Be(redisTime);
    }

    // ── Rejection path: OrphanRestoredAt cleared ──────────────────────────

    [Fact]
    public void TransitionToIdle_RejectionPath_AgentWithOrphanRestoredAt_ClearsInMemoryFieldAndFiresRedisWrite()
    {
        // Verifies the bug fix: ResetAgentToIdle previously did not clear OrphanRestoredAt,
        // leaving a stale flag that gates crash recovery in AgentOrphanRecoveryService
        // (HandleCrashRecoveryAsync: `if (message.ActiveJob is null && entry.OrphanRestoredAt is null)`).
        var agent = MakeAgent();
        agent.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        _sut.TransitionToIdle(agent, run: null, jobIdValue: "", callerContext: "ResetAgentToIdle");

        agent.OrphanRestoredAt.Should().BeNull();
        _facade.Verify(f => f.UpdateAgentFieldAsync(agent.AgentId, "orphanRestoredAt", null), Times.Once);
    }

    // ── Agent null, run has AgentId (fallback path) ────────────────────────

    [Fact]
    public void TransitionToIdle_AgentNull_RunHasAgentId_CallsTransitionStatusIdle()
    {
        var run = MakeRun("agent-42");
        var expectedAgentId = new AgentId("agent-42");

        _sut.TransitionToIdle(agent: null, run, jobIdValue: "job-1", callerContext: "test");

        _facade.Verify(f => f.TransitionStatus(expectedAgentId, AgentStatus.Idle), Times.Once);
    }

    [Fact]
    public void TransitionToIdle_AgentNull_RunHasAgentId_ClearsActiveJobIdField()
    {
        var run = MakeRun("agent-42");
        var expectedAgentId = new AgentId("agent-42");

        _sut.TransitionToIdle(agent: null, run, jobIdValue: "job-1", callerContext: "test");

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

        _sut.TransitionToIdle(agent: null, run, jobIdValue: "job-1", callerContext: "test");

        // Template: "[{CallerContext}] agent lookup returned null for job {JobId} (agentId={AgentId}) ..."
        // Compiler selects Warning<T0,T1,T2>(string, T0, T1, T2): T0=callerContext, T1=jobIdValue, T2=run.AgentId.
        _logger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("agent lookup returned null") && s.Contains("{JobId}") && s.Contains("{AgentId}")),
                It.IsAny<string>(),   // T0 = callerContext
                It.IsAny<string>(),   // T1 = jobIdValue
                It.IsAny<string>()),  // T2 = run.AgentId
            Times.Once);
    }

    // ── No-op paths ────────────────────────────────────────────────────────

    [Fact]
    public void TransitionToIdle_AgentNull_RunIsNull_NoOp()
    {
        _sut.TransitionToIdle(agent: null, run: null, jobIdValue: "job-1", callerContext: "test");

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

        _sut.TransitionToIdle(agent: null, run, jobIdValue: "job-1", callerContext: "test");

        _facade.Verify(f => f.TransitionStatus(It.IsAny<AgentId>(), It.IsAny<AgentStatus>()), Times.Never);
    }
}
