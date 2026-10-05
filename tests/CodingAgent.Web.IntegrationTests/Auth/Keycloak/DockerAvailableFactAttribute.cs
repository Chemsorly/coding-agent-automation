namespace CodingAgent.Web.IntegrationTests.Auth.Keycloak;

/// <summary>
/// A <see cref="FactAttribute"/> that skips the test when Docker is not available on the host.
/// Keycloak integration tests require Testcontainers and therefore a running Docker daemon.
/// In environments without Docker (e.g. the local quality-gate runner), these tests are skipped
/// rather than failed. The CI <c>iam-tests</c> job runs them in an environment with Docker.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DockerAvailableFactAttribute : FactAttribute
{
    private static readonly string? SkipReason = ComputeSkipReason();

    public DockerAvailableFactAttribute()
    {
        if (SkipReason is not null)
            Skip = SkipReason;
    }

    private static string? ComputeSkipReason()
    {
        // Docker is considered available when its default Unix socket exists and is accessible.
        // This mirrors the check Testcontainers performs before attempting to build a container.
        const string dockerSocket = "/var/run/docker.sock";
        return File.Exists(dockerSocket) ? null : "Docker is not available (no socket at /var/run/docker.sock). Run in an environment with Docker to execute this test.";
    }
}
