using AwesomeAssertions;
using CodingAgent.Api;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Validates the structural ordering enforcement and idempotency guarantees introduced
/// by the extraction of <see cref="ApiServiceCollectionExtensions"/> sub-methods into
/// dedicated extension classes (issue #4021).
///
/// These tests do NOT build a full service provider — they inspect the
/// <see cref="IServiceCollection"/> descriptor list directly, so they are lightweight
/// and do not require Postgres, Redis, or Kubernetes to be available.
/// </summary>
[Trait("Feature", "DiOrdering")]
public sealed class ApiServiceExtensionsOrderingTests
{
    /// <summary>
    /// Calling <see cref="ApiLifecycleServiceExtensions.AddApiLifecycleAndConsolidation"/>
    /// without an explicit prior call to <see cref="ApiTokenVendingServiceExtensions.AddApiTokenVending"/>
    /// must still register <see cref="ITokenVendingService"/> (structural enforcement),
    /// and must also register lifecycle-specific services such as
    /// <see cref="IConsolidationJobPreparationService"/> (postcondition verification).
    ///
    /// This validates that the structural enforcement is not vacuous — the method registers
    /// its own services and does not merely delegate to <c>AddApiTokenVending</c>.
    /// </summary>
    [Fact]
    public void AddApiLifecycleAndConsolidation_WithoutExplicitTokenVendingCall_RegistersITokenVendingServiceAndOwnServices()
    {
        // Arrange: start with a fresh service collection — no prior AddApiTokenVending call.
        var services = new ServiceCollection();

        // Act: call only AddApiLifecycleAndConsolidation.
        services.AddApiLifecycleAndConsolidation();

        // Assert precondition: ITokenVendingService is registered because the structural call to
        // AddApiTokenVending fired internally.
        var hasTokenVending = services.Any(sd => sd.ServiceType == typeof(ITokenVendingService));
        hasTokenVending.Should().BeTrue(
            "AddApiLifecycleAndConsolidation must structurally call AddApiTokenVending to ensure " +
            "ITokenVendingService is registered before IConsolidationJobPreparationService " +
            "is registered, regardless of the caller's call order.");

        // Assert postcondition: lifecycle-specific services were also registered, confirming
        // the method did not merely delegate to AddApiTokenVending.
        var hasConsolidation = services.Any(sd => sd.ServiceType == typeof(IConsolidationJobPreparationService));
        hasConsolidation.Should().BeTrue(
            "AddApiLifecycleAndConsolidation must register IConsolidationJobPreparationService " +
            "as one of its own responsibilities.");
    }

    /// <summary>
    /// Calling <see cref="ApiTokenVendingServiceExtensions.AddApiTokenVending"/> twice on the
    /// same <see cref="IServiceCollection"/> must not produce duplicate service registrations
    /// for <see cref="ITokenVendingService"/>.
    ///
    /// This validates the idempotency marker: the second call must be a no-op.
    /// </summary>
    [Fact]
    public void AddApiTokenVending_CalledTwice_RegistersITokenVendingServiceExactlyOnce()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act: call AddApiTokenVending twice.
        services.AddApiTokenVending();
        services.AddApiTokenVending();

        // Assert: exactly one ITokenVendingService registration — the second call was a no-op.
        var count = services.Count(sd => sd.ServiceType == typeof(ITokenVendingService));
        count.Should().Be(1,
            "the idempotency marker must prevent AddApiTokenVending from registering " +
            "ITokenVendingService more than once when called multiple times on the same " +
            "IServiceCollection (e.g. when AddApiLifecycleAndConsolidation calls it " +
            "structurally after the coordinator already called it explicitly).");
    }

    /// <summary>
    /// Calling <see cref="ApiDispatchServiceExtensions.AddApiDispatch"/>
    /// without an explicit prior call to <see cref="ApiKubernetesServiceExtensions.AddApiKubernetes"/>
    /// must still register <see cref="IKubernetesJobClient"/> (structural enforcement).
    ///
    /// This validates the <c>AddApiDispatch → AddApiKubernetes</c> structural call that mirrors
    /// the lifecycle/token-vending pattern verified by the tests above.
    /// </summary>
    [Fact]
    public void AddApiDispatch_WithoutExplicitKubernetesCall_RegistersIKubernetesJobClient()
    {
        // Arrange: start with a fresh service collection — no prior AddApiKubernetes call.
        var services = new ServiceCollection();

        // Act: call only AddApiDispatch.
        services.AddApiDispatch();

        // Assert: IKubernetesJobClient is registered because the structural call to
        // AddApiKubernetes fired internally.
        var hasJobClient = services.Any(sd => sd.ServiceType == typeof(IKubernetesJobClient));
        hasJobClient.Should().BeTrue(
            "AddApiDispatch must structurally call AddApiKubernetes to ensure " +
            "IKubernetesJobClient is registered before DispatchLifecycleService " +
            "is registered, regardless of the caller's call order.");
    }

    /// <summary>
    /// Calling <see cref="ApiKubernetesServiceExtensions.AddApiKubernetes"/> twice on the
    /// same <see cref="IServiceCollection"/> must not produce duplicate service registrations
    /// for <see cref="IKubernetesJobClient"/>.
    ///
    /// This validates the idempotency marker on the <c>AddApiKubernetes → AddApiDispatch</c> chain:
    /// when AddApiDispatch calls AddApiKubernetes structurally after the coordinator already called
    /// it, the second invocation must be a no-op.
    /// </summary>
    [Fact]
    public void AddApiKubernetes_CalledTwice_RegistersIKubernetesJobClientExactlyOnce()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act: call AddApiKubernetes twice.
        services.AddApiKubernetes();
        services.AddApiKubernetes();

        // Assert: exactly one IKubernetesJobClient registration — the second call was a no-op.
        var count = services.Count(sd => sd.ServiceType == typeof(IKubernetesJobClient));
        count.Should().Be(1,
            "the idempotency marker must prevent AddApiKubernetes from registering " +
            "IKubernetesJobClient more than once when called multiple times on the same " +
            "IServiceCollection (e.g. when AddApiDispatch calls it structurally after the " +
            "coordinator already called it explicitly).");
    }
}
