using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Serilog.Events;

namespace CodingAgent.Pipeline.UnitTests.Dispatch;

/// <summary>Spec 057 DD3, req 2.4, 3.3, 3.6: the agent start gate's lock, count and limit inputs.</summary>
public sealed class AgentStartGateTests
{
    private const string Templates = """
        - labels: "kiro,dotnet"
          image: "agent:test"
          providerType: "kiro"
          maxConcurrent: 4
        - labels: "claude"
          image: "agent:test"
          providerType: "claude"
        """;

    private readonly RecordingLockProvider _locks = new();
    private readonly Mock<IKubernetesJobClient> _jobs = new();
    private readonly IDbContextFactory<PipelineDbContext> _db = new GateTestDbFactory($"gate-{Guid.NewGuid():N}");

    private AgentStartGate CreateGate(
        IDbContextFactory<PipelineDbContext>? capacityDb = null, int helmGlobal = 0, string templates = Templates,
        Serilog.ILogger? log = null) =>
        new(_locks, _jobs.Object, JobTemplateStore.LoadFromYaml(templates),
            new DispatchServiceOptions { Namespace = "test", MaxConcurrentAgents = helmGlobal },
            new AgentCapacityStore(capacityDb ?? _db), log ?? Serilog.Log.Logger);

    private static V1Job RunningJob(string label) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Labels = new Dictionary<string, string> { ["caa/agent-selector"] = label, ["caa/work-item-id"] = Guid.NewGuid().ToString() }
        },
        Status = new V1JobStatus()
    };

    private void ListJobs(params V1Job[] jobs) =>
        _jobs.Setup(j => j.ListJobsAsync("test", AgentJobPvcs.AgentJobSelector, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1JobList { Items = jobs });

    // req 3.3, D8
    [Fact]
    public async Task EnterAsync_TakesTheOneDispatchAgentsLock_AndReleasesItOnDispose()
    {
        ListJobs();
        var lease = await CreateGate().EnterAsync(CancellationToken.None);

        _locks.Names.Should().Equal("dispatch-agents");
        _locks.Released.Should().Be(0);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        _locks.Released.Should().Be(1, "disposing the lease releases the lock once");
    }

    // req 2.4
    [Fact]
    public async Task EnterAsync_WhenTheJobListFails_ThrowsAgentCountUnavailable_AndReleasesTheLock()
    {
        _jobs.Setup(j => j.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("api server down"));

        var act = () => CreateGate().EnterAsync(CancellationToken.None);

        await act.Should().ThrowAsync<AgentCountUnavailableException>();
        _locks.Released.Should().Be(1);
    }

    // req 2.4: a client timeout surfaces as a cancellation while the caller's token is not cancelled.
    [Fact]
    public async Task EnterAsync_WhenTheJobListTimesOut_ThrowsAgentCountUnavailable_AndReleasesTheLock()
    {
        _jobs.Setup(j => j.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("HTTP timeout"));

        var act = () => CreateGate().EnterAsync(CancellationToken.None);

        await act.Should().ThrowAsync<AgentCountUnavailableException>();
        _locks.Released.Should().Be(1);
    }

    // review-1 B1: a cancelled start must not leak the lock.
    [Fact]
    public async Task EnterAsync_WhenTheCallerCancels_PropagatesTheCancellation_AndReleasesTheLock()
    {
        using var cts = new CancellationTokenSource();
        _jobs.Setup(j => j.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, CancellationToken ct) =>
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return new V1JobList { Items = [] };
            });

        var act = () => CreateGate().EnterAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _locks.Released.Should().Be(1);
    }

    // review-1 B1: a failing read of the UI limits must not leak the lock.
    [Fact]
    public async Task EnterAsync_WhenTheUiLimitsCannotBeRead_PropagatesTheError_AndReleasesTheLock()
    {
        ListJobs();
        var act = () => CreateGate(capacityDb: new ThrowingDbFactory()).EnterAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _locks.Released.Should().Be(1);
    }

    // req 3.6: the UI limits are read at every start, so a save applies without a restart.
    [Fact]
    public async Task EnterAsync_ReadsTheUiLimitsAtEveryStart()
    {
        ListJobs(RunningJob("claude"));
        var gate = CreateGate();
        var store = new AgentCapacityStore(_db);

        await using (var lease = await gate.EnterAsync(CancellationToken.None))
            lease.CheckWorkItem("claude").Allowed.Should().BeTrue("no limit is set yet");

        await store.SaveAsync(new AgentCapacityLimits
        {
            MaxConcurrentByJobTemplate = new Dictionary<string, int>(StringComparer.Ordinal) { ["claude"] = 1 }
        }, CancellationToken.None);

        await using (var lease = await gate.EnterAsync(CancellationToken.None))
            lease.CheckWorkItem("claude").Should().Be(new AgentStartDecision(false, LimitScope.Template, LimitSource.Ui, 1, 1));
    }

    // req 1.1, 1.2: the Helm limits come from the job templates and the options.
    [Fact]
    public async Task Lease_UsesTheHelmLimits_AndTheRunningCountFromTheJobs()
    {
        ListJobs(RunningJob("dotnet.kiro"), RunningJob("dotnet.kiro"), RunningJob("dotnet.kiro"), RunningJob("dotnet.kiro"), RunningJob("claude"));

        await using (var lease = await CreateGate().EnterAsync(CancellationToken.None))
        {
            lease.CheckWorkItem("dotnet,kiro").Should().Be(new AgentStartDecision(false, LimitScope.Template, LimitSource.Helm, 4, 4));
            lease.CheckChat("dotnet,kiro").Allowed.Should().BeFalse("the Helm template limit refuses a chat too");
            lease.CheckWorkItem("claude").Allowed.Should().BeTrue();
            lease.Jobs.Counts.Total.Should().Be(5);
        }

        await using (var lease = await CreateGate(helmGlobal: 5).EnterAsync(CancellationToken.None))
            lease.CheckWorkItem("claude").Should().Be(new AgentStartDecision(false, LimitScope.Global, LimitSource.Helm, 5, 5));
    }

    // req 3.3, 10.2: a work-item start and a chat start racing for the last slot create exactly one Job, because both
    // take the one lock from the count to the Job's creation.
    [Fact]
    public async Task ConcurrentStarts_ForTheLastSlot_CreateExactlyOneJob()
    {
        for (var round = 0; round < 25; round++)
        {
            var jobs = new List<V1Job>();
            var client = new Mock<IKubernetesJobClient>();
            client.Setup(j => j.ListJobsAsync("test", AgentJobPvcs.AgentJobSelector, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { lock (jobs) return new V1JobList { Items = [.. jobs] }; });
            var gate = new AgentStartGate(new InProcessDistributedLockProvider(), client.Object,
                JobTemplateStore.LoadFromYaml(Templates), new DispatchServiceOptions { Namespace = "test", MaxConcurrentAgents = 1 },
                new AgentCapacityStore(_db), Serilog.Log.Logger);

            async Task<bool> StartAsync(bool chat)
            {
                await using var lease = await gate.EnterAsync(CancellationToken.None);
                var decision = chat ? lease.CheckChat("claude") : lease.CheckWorkItem("dotnet,kiro");
                if (!decision.Allowed)
                    return false;
                await Task.Yield(); // the Job is created while the lock is held
                lock (jobs) jobs.Add(RunningJob(chat ? "claude" : "dotnet.kiro"));
                return true;
            }

            var results = await Task.WhenAll(Task.Run(() => StartAsync(chat: false)), Task.Run(() => StartAsync(chat: true)));

            results.Count(started => started).Should().Be(1, $"round {round}: only one start may pass a global limit of 1");
            jobs.Should().HaveCount(1);
        }
    }

    // DD2
    [Fact]
    public void Constructor_WhenTwoTemplateKeysShareAJobLabel_LogsAWarning()
    {
        var events = new List<LogEvent>();
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new ListSink(events)).CreateLogger();
        const string colliding = """
            - labels: "a.b"
              image: "agent:test"
              providerType: "kiro"
            - labels: "a,b"
              image: "agent:test"
              providerType: "kiro"
            """;

        _ = CreateGate(templates: colliding, log: log);

        events.Should().ContainSingle(e => e.Level == LogEventLevel.Warning
            && e.MessageTemplate.Text.Contains("share the Job label"));
    }

    private sealed class RecordingLockProvider : IDistributedLockProvider
    {
        public List<string> Names { get; } = [];
        public int Released;

        public Task<IAsyncDisposable> AcquireAsync(string lockName, CancellationToken ct = default)
        {
            Names.Add(lockName);
            return Task.FromResult<IAsyncDisposable>(new Handle(this));
        }

        private sealed class Handle(RecordingLockProvider owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.Released);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ListSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }

    private sealed class ThrowingDbFactory : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() => throw new InvalidOperationException("database down");
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromException<PipelineDbContext>(new InvalidOperationException("database down"));
    }

    private sealed class GateTestDbFactory(string name) : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() => new(new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
    }
}
