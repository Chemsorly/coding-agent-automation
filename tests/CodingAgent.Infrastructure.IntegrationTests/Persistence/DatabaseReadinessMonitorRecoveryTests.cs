using AwesomeAssertions;
using CodingAgent.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Testcontainers.PostgreSql;

namespace CodingAgent.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Integration test covering the recovery branch of
/// <see cref="DatabaseReadinessMonitor.ExecuteAsync"/>: after the service has been
/// pre-marked unhealthy, a successful probe against a real PostgreSQL instance must
/// call <c>MarkHealthy</c> and log an informational message.
///
/// <para>
/// This test requires Docker.  When Docker is unavailable the test returns early
/// via the <see cref="DatabaseReadinessMonitorFixture.IsDockerUnavailable"/> guard,
/// consistent with the other integration tests in this project.  CI runs these
/// tests in the Docker-enabled integration job.
/// </para>
///
/// <para>
/// TODO: The <c>MarkHealthy()</c> call and the "Database connectivity restored" log line
/// in <c>DatabaseReadinessMonitor.cs</c> will not contribute to Sonar's line-coverage
/// metric because the Sonar analysis excludes the Integration test category
/// (only Unit tests count; see <c>sonar-coverage.runsettings</c>).  This is structurally
/// unavoidable without making <c>ProbeDatabaseAsync</c> injectable (which would require
/// removing <c>sealed</c> and changing its visibility — a production-code change prohibited
/// by the issue's acceptance criteria).  The behaviour IS verified here against a real
/// database; the gap is in coverage reporting only.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DatabaseReadinessMonitorRecoveryTests
    : IClassFixture<DatabaseReadinessMonitorFixture>
{
    private readonly DatabaseReadinessMonitorFixture _fixture;

    public DatabaseReadinessMonitorRecoveryTests(DatabaseReadinessMonitorFixture fixture)
    {
        _fixture = fixture;
    }

    // ── AC2: recovery path ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AfterPremarkedUnhealthy_SuccessfulProbeMarksHealthyAndLogsInformation()
    {
        // Skip gracefully when Docker is unavailable.
        if (_fixture.IsDockerUnavailable) return;
        _fixture.InitializationException.Should().BeNull(
            "the PostgreSQL container must start cleanly");

        var healthState = new DatabaseHealthState();
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        // Simulate a prior failure: the monitor is already in "unhealthy" state when it starts.
        healthState.MarkUnhealthy();

        var sut = new DatabaseReadinessMonitor(
            healthState,
            _fixture.ConnectionString,
            logger,
            checkInterval: TimeSpan.FromMilliseconds(50));

        // Overall guard: if the state never recovers in 15 s something is very wrong.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sut.StartAsync(cts.Token);

        // Poll until the monitor restores healthy state
        // (startup delay ~2 s + one successful probe against the container).
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!healthState.IsDatabaseHealthy && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50, CancellationToken.None);

        await sut.StopAsync(CancellationToken.None);

        healthState.IsDatabaseHealthy.Should().BeTrue(
            "a successful probe after an unhealthy state must call MarkHealthy");

        sink.Events.Should().Contain(
            e => e.Level == LogEventLevel.Information
              && e.MessageTemplate.Text.Contains("Database connectivity restored"),
            "an Information log must be emitted when connectivity is restored");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Thread-safe Serilog sink that collects log events for assertion.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        private readonly object _lock = new();
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get { lock (_lock) { return [.. _events]; } }
        }

        public void Emit(LogEvent logEvent) { lock (_lock) { _events.Add(logEvent); } }
    }
}

/// <summary>
/// Starts a single PostgreSQL container for the
/// <see cref="DatabaseReadinessMonitorRecoveryTests"/> class. No schema migrations
/// are applied — the monitor only executes <c>SELECT 1</c> so an empty database is
/// sufficient.
/// </summary>
public sealed class DatabaseReadinessMonitorFixture : IAsyncLifetime
{
    // Null until InitializeAsync runs: building the container in a field initialiser
    // would throw in Docker-less environments before trait filtering can skip these tests.
    private PostgreSqlContainer? _container;

    /// <summary>Set when the container fails to start.</summary>
    public Exception? InitializationException { get; private set; }

    /// <summary>True when the container could not be built (typically Docker is unavailable).</summary>
    public bool IsDockerUnavailable => _container is null;

    /// <summary>The connection string for the running container.</summary>
    public string ConnectionString =>
        _container?.GetConnectionString()
        ?? throw new InvalidOperationException("The PostgreSQL container is not available.");

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("readiness_test")
                .WithUsername("test")
                .WithPassword("test")
                .Build();

            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            InitializationException = ex;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
