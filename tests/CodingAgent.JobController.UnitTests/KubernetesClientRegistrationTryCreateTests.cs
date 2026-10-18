using AwesomeAssertions;
using k8s;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.JobController.UnitTests;

/// <summary>
/// Tests for <see cref="KubernetesClientRegistration.TryCreate"/> and characterizes the
/// throw-on-failure semantics of the existing <see cref="KubernetesClientRegistration.AddKubernetesClient"/>
/// to guard against accidental modification.
///
/// <para>
/// All tests run in a non-Kubernetes environment (CI, local dev without a cluster), where
/// <c>KubernetesClientConfiguration.IsInCluster()</c> returns <c>false</c> and
/// <c>BuildDefaultConfig()</c> falls back to host <c>http://localhost:8080</c>.
/// These are the exact conditions under which the sentinel-check null-return paths exercise.
/// </para>
///
/// <para>
/// TODO: The tests below are environment-gated: a developer with KUBECONFIG set to a live cluster
/// will see TryCreate return a non-null client, causing the null assertions to fail. These tests
/// can only be made fully hermetic by wrapping the static KubernetesClientConfiguration calls
/// behind an interface or delegate seam so the cluster resolution can be substituted in tests.
/// Until that seam is introduced, these tests should be run in CI-only (no kubeconfig) environments.
/// </para>
///
/// <para>
/// TODO: The catch path of TryCreate (lines under the catch block in KubernetesClientRegistration)
/// and the callerNote/callerLabel log-message content are not covered by any test. Exercising those
/// paths requires either (a) making KubernetesClientConfiguration calls injectable, or (b) wiring
/// an in-memory Serilog sink and arranging for InClusterConfig/BuildDefaultConfig to throw.
/// Until a seam is available, these paths remain a coverage gap.
/// </para>
/// </summary>
public sealed class KubernetesClientRegistrationTryCreateTests
{
    [Fact]
    public void TryCreate_InNonK8sEnvironment_ReturnsNull()
    {
        // In CI / local dev with no cluster, BuildDefaultConfig() returns http://localhost:8080.
        // TryCreate must return null rather than throwing (degrade-gracefully semantics).
        var result = KubernetesClientRegistration.TryCreate("Test");

        result.Should().BeNull(
            "TryCreate returns null when the resolved host is the http://localhost:8080 sentinel");
    }

    [Fact]
    public void TryCreate_WithApiCallerLabel_ReturnsNull_InNonK8sEnvironment()
    {
        // Confirms the API-specific call path also degrades gracefully to null.
        var result = KubernetesClientRegistration.TryCreate(
            callerLabel: "API",
            callerNote: "DatabaseMaintenanceService sweep runs ungated.");

        result.Should().BeNull(
            "TryCreate with callerLabel='API' must still return null when no cluster is reachable");
    }

    [Fact]
    public void TryCreate_WithSchedulerCallerLabel_ReturnsNull_InNonK8sEnvironment()
    {
        // Confirms the Scheduler-specific call path also degrades gracefully to null.
        var result = KubernetesClientRegistration.TryCreate(callerLabel: "Scheduler");

        result.Should().BeNull(
            "TryCreate with callerLabel='Scheduler' must still return null when no cluster is reachable");
    }

    [Fact]
    public void TryCreate_WithCallerNote_DoesNotAffectNullReturn_InNonK8sEnvironment()
    {
        // callerNote is only used in the catch-path log message; it must not alter the return value.
        var withNote    = KubernetesClientRegistration.TryCreate("Test", "Some diagnostic note.");
        var withoutNote = KubernetesClientRegistration.TryCreate("Test");

        withNote.Should().BeNull("callerNote must not change the null-return behavior");
        withoutNote.Should().BeNull("omitting callerNote must not change the null-return behavior");
    }

    /// <summary>
    /// Characterization test: <see cref="KubernetesClientRegistration.AddKubernetesClient"/> must
    /// still throw <see cref="InvalidOperationException"/> when no usable cluster is configured.
    /// This is the fail-fast behaviour required by the JobController host and must not be affected
    /// by the introduction of <see cref="KubernetesClientRegistration.TryCreate"/>.
    /// </summary>
    [Fact]
    public void AddKubernetesClient_ThrowsInvalidOperationException_InNonK8sEnvironment()
    {
        var services = new ServiceCollection();
        services.AddKubernetesClient();
        using var provider = services.BuildServiceProvider();

        // The factory lambda registered by AddKubernetesClient throws InvalidOperationException
        // when the resolved host is http://localhost:8080. The DI container propagates this directly.
        var act = () => provider.GetRequiredService<IKubernetes>();

        act.Should().Throw<InvalidOperationException>(
            "AddKubernetesClient uses fail-fast semantics for the JobController host — " +
            "it must throw when no usable Kubernetes configuration is available");
    }
}
