using CodingAgentWebUI;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests startup failure scenarios for WorkDistributionRegistration:
/// - Unrecognized mode → InvalidOperationException
/// - Kubernetes mode outside cluster → InvalidOperationException
/// Validates: Requirements 1.9, 8.6
/// </summary>
public class WorkDistributionRegistrationTests
{
    [Theory]
    [InlineData("InvalidMode")]
    [InlineData("Docker")]
    [InlineData("")]
    [InlineData("k8s")]
    [InlineData("signalr-push")]
    public void AddWorkDistribution_UnrecognizedMode_ThrowsInvalidOperationException(string invalidMode)
    {
        // Arrange: DB connection string is set (triggers mode validation) with invalid mode
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Host"] = "localhost",
                ["Database:Name"] = "test",
                ["WorkDistribution:Mode"] = invalidMode,
            })
            .Build();

        var services = new ServiceCollection();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddWorkDistribution(config));

        Assert.Contains("Unrecognized WorkDistribution:Mode", ex.Message);
        Assert.Contains(invalidMode, ex.Message);
        Assert.Contains("SignalR", ex.Message);
        Assert.Contains("Kubernetes", ex.Message);
    }

    [Fact]
    [Trait("Category", "RequiresNonK8sEnvironment")]
    public void AddWorkDistribution_KubernetesModeOutsideCluster_ThrowsInvalidOperationException()
    {
        // Skip if running inside K8s (CI runners with service account token mounted)
        if (File.Exists("/var/run/secrets/kubernetes.io/serviceaccount/token"))
            return;

        // Arrange: DB connection string set, mode is Kubernetes, but we're not in a cluster
        // (no /var/run/secrets/kubernetes.io/serviceaccount/token and no KUBERNETES_SERVICE_HOST env var)
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Host"] = "localhost",
                ["Database:Name"] = "test",
                ["WorkDistribution:Mode"] = "Kubernetes",
            })
            .Build();

        var services = new ServiceCollection();

        // Ensure KUBERNETES_SERVICE_HOST is not set (it won't be in unit test environment)
        var originalValue = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        try
        {
            Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", null);

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(
                () => services.AddWorkDistribution(config));

            Assert.Contains("not running inside a Kubernetes cluster", ex.Message);
            Assert.Contains("service account token", ex.Message);
        }
        finally
        {
            // Restore original value
            Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", originalValue);
        }
    }

    [Fact]
    public void AddWorkDistribution_ValidSignalRModeWithDb_DoesNotThrowModeValidationError()
    {
        // Arrange: Valid SignalR mode with DB — should NOT throw on mode validation
        // (will fail later on missing service registrations, but mode validation passes)
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Host"] = "localhost",
                ["Database:Name"] = "test",
                ["WorkDistribution:Mode"] = "SignalR",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Act — should not throw InvalidOperationException about mode
        // It may throw about missing dependencies, but that's not mode validation
        Exception? thrownEx = null;
        try
        {
            services.AddWorkDistribution(config);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Unrecognized WorkDistribution:Mode")
                                                    || ex.Message.Contains("not running inside a Kubernetes cluster"))
        {
            thrownEx = ex;
        }
        catch
        {
            // Other exceptions (missing DI registrations) are fine — mode validation passed
        }

        Assert.Null(thrownEx);
    }

    [Fact]
    public void AddWorkDistribution_LegacyModeWithoutConnectionString_DoesNotValidateMode()
    {
        // Arrange: No connection string → legacy mode. Mode value is irrelevant.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Mode"] = "SomeGarbageValue",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Legacy mode needs IJobDispatcher, JobDispatcherService, IOrchestratorRunService
        var logger = Serilog.Log.Logger;
        var registry = new AgentRegistryService(logger);
        services.AddSingleton(Mock.Of<CodingAgentWebUI.Pipeline.Interfaces.IJobDispatcher>());
        services.AddSingleton(new JobDispatcherService(registry, logger));
        services.AddSingleton(Mock.Of<CodingAgentWebUI.Pipeline.Interfaces.IOrchestratorRunService>());

        // Act — should not throw about mode validation (legacy mode skips it)
        Exception? thrownEx = null;
        try
        {
            services.AddWorkDistribution(config);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Unrecognized WorkDistribution:Mode"))
        {
            thrownEx = ex;
        }
        catch
        {
            // Other exceptions are acceptable
        }

        Assert.Null(thrownEx);
    }
}
