using System.Net.Sockets;
using Xunit;

namespace CodingAgent.Web.IntegrationTests.Auth.Keycloak;

/// <summary>
/// A <see cref="FactAttribute"/> that skips the test at discovery time when Docker is not
/// available in the current environment. Prevents the IAM integration tests (which require a
/// running Docker daemon for Testcontainers) from appearing as hard failures in environments
/// that do not have Docker (e.g. the local quality-gate runner). The tests still run correctly
/// in the CI <c>iam-tests</c> job where Docker is available.
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
    /// Returns null when Docker is reachable; returns a skip reason when it is not.
    /// </summary>
    private static string? ComputeSkipReason()
    {
        const string SocketPath = "/var/run/docker.sock";
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            return null; // Docker is available
        }
        catch
        {
            return "Docker is unavailable — IAM tests require Docker (CI job: iam-tests).";
        }
    }
}
