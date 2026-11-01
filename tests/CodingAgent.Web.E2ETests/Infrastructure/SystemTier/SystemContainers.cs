using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace CodingAgent.Web.E2ETests.Infrastructure.SystemTier;

/// <summary>
/// Starts and manages the real Postgres and Redis containers shared across all
/// <see cref="SystemE2ECollection"/> tests. One instance is created per test collection
/// run (xUnit's collection fixture lifecycle).
///
/// <para>
/// Fixed host ports are reserved before the containers are built so that stopped-and-restarted
/// containers (used by failure-scenario tests) bind to the same port and the running API hosts
/// continue to connect correctly.
/// </para>
/// </summary>
public sealed class SystemContainers : IAsyncLifetime
{
    public const string PostgresUser = "test";
    public const string PostgresPassword = "test";
    public const string PostgresDatabase = "system_e2e";

    private PostgreSqlContainer? _postgres;
    private RedisContainer? _redis;
    private int _postgresPort;
    private int _redisPort;

    /// <summary>Hostname of the running Postgres container (typically "localhost").</summary>
    public string PostgresHost => _postgres!.Hostname;

    /// <summary>Fixed host port Postgres is bound to.</summary>
    public int PostgresPort => _postgresPort;

    /// <summary>Connection string for Redis (host:port).</summary>
    public string RedisConnectionString => $"{_redis!.Hostname}:{_redisPort}";

    public async Task InitializeAsync()
    {
        // If Docker is unavailable, return early without building anything. When
        // E2E_REQUIRE_DOCKER=true (CI), RequiresDockerFactAttribute does not skip the test,
        // so EnsureRunningAsync will throw and the test will fail — which is the intended
        // behaviour for a misconfigured runner.
        const string SocketPath = "/var/run/docker.sock";
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
        }
        catch
        {
            return; // Docker unavailable; containers remain null
        }

        // Reserve two free host ports. Keep both TcpListeners active until both port numbers
        // are recorded — releasing the first before starting the second allows the OS to reuse
        // the same port number, causing one container to fail with "address already in use".
        var (port1, port2) = ReserveTwoFreePorts();
        _postgresPort = port1;
        _redisPort = port2;

        _postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase(PostgresDatabase)
            .WithUsername(PostgresUser)
            .WithPassword(PostgresPassword)
            .WithPortBinding(_postgresPort, 5432)
            .Build();

        _redis = new RedisBuilder("redis:7-alpine")
            .WithPortBinding(_redisPort, 6379)
            .Build();

        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
        if (_redis is not null)
            await _redis.DisposeAsync();
    }

    /// <summary>Stops the Redis container (for failure-scenario tests).</summary>
    public Task StopRedisAsync() => _redis!.StopAsync();

    /// <summary>Starts the Redis container (to recover after a stop).</summary>
    public Task StartRedisAsync() => _redis!.StartAsync();

    /// <summary>Stops the Postgres container (for failure-scenario tests).</summary>
    public Task StopPostgresAsync() => _postgres!.StopAsync();

    /// <summary>Starts the Postgres container (to recover after a stop).</summary>
    public Task StartPostgresAsync() => _postgres!.StartAsync();

    /// <summary>
    /// Starts whichever containers are not currently in the <see cref="TestcontainersStates.Running"/>
    /// state. Called by <see cref="SystemE2ETestBase.InitializeAsync"/> before each test so a
    /// failed test that stopped a container cannot break subsequent tests.
    /// </summary>
    public async Task EnsureRunningAsync()
    {
        if (_postgres is null || _redis is null)
            throw new InvalidOperationException(
                "SystemContainers were not started. " +
                "Docker may be unavailable on this host. " +
                "This test requires a running Docker daemon (CI job: e2e-system).");

        var tasks = new List<Task>();
        if (_postgres.State != TestcontainersStates.Running)
            tasks.Add(_postgres.StartAsync());
        if (_redis.State != TestcontainersStates.Running)
            tasks.Add(_redis.StartAsync());
        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Flushes all keys in Redis. Called before each test to prevent state from a
    /// previous test leaking into the next one.
    /// </summary>
    public Task FlushRedisAsync() =>
        _redis!.ExecAsync(["redis-cli", "FLUSHALL"]);

    private static (int Port1, int Port2) ReserveTwoFreePorts()
    {
        // Hold both listeners open simultaneously so the OS cannot reuse the first port
        // for the second binding. Only release after both ports are recorded.
        var l1 = new TcpListener(IPAddress.Loopback, 0);
        var l2 = new TcpListener(IPAddress.Loopback, 0);
        l1.Start();
        l2.Start();
        var port1 = ((IPEndPoint)l1.LocalEndpoint).Port;
        var port2 = ((IPEndPoint)l2.LocalEndpoint).Port;
        l1.Stop();
        l2.Stop();
        return (port1, port2);
    }
}
