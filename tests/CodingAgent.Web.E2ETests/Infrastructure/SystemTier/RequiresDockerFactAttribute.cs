using System.Net.Sockets;
using Xunit;

namespace CodingAgent.Web.E2ETests.Infrastructure.SystemTier;

/// <summary>
/// A <see cref="FactAttribute"/> that skips the test at discovery time when Docker is not
/// available in the current environment. Prevents the System E2E tests (which require a
/// running Docker daemon for Testcontainers) from appearing as hard failures in environments
/// that do not have Docker (e.g. agent pods, local dev without Docker).
///
/// <para>
/// When the environment variable <c>E2E_REQUIRE_DOCKER</c> is set to <c>true</c>, Docker
/// absence causes the test to <em>fail</em> rather than skip — used by the
/// <c>e2e-system</c> CI job to prevent silent no-ops on a broken runner.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    private static readonly string? SkipReason = ComputeSkipReason();

    public RequiresDockerFactAttribute()
    {
        if (SkipReason is not null)
            Skip = SkipReason;
    }

    /// <summary>
    /// Probes the Docker Unix socket to determine whether Docker is available.
    /// Returns null when Docker is reachable or when <c>E2E_REQUIRE_DOCKER=true</c>;
    /// returns a skip reason when Docker is not reachable and the env var is not set.
    /// </summary>
    private static string? ComputeSkipReason()
    {
        // When E2E_REQUIRE_DOCKER=true (set by the e2e-system CI job), never skip.
        // Docker absence will fail the test at runtime via SystemContainers, which is
        // the desired behaviour for CI: a missing Docker daemon is a runner misconfiguration.
        if (string.Equals(
                Environment.GetEnvironmentVariable("E2E_REQUIRE_DOCKER"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        const string SocketPath = "/var/run/docker.sock";
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            return null; // Docker is available
        }
        catch
        {
            return "Docker is unavailable: System tests need Docker (CI job: e2e-system).";
        }
    }
}
