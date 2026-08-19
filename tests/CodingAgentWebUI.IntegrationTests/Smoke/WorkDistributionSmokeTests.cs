using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.IntegrationTests.Smoke;

/// <summary>
/// Smoke tests for work distribution DI registrations that must survive the deletion of
/// WorkDistributionRegistration.Kubernetes.cs (Spec 043 Task 9).
/// Uses DbModeWebApplicationFactory which boots the full K8s-mode stack with InMemory DB.
/// </summary>
[Collection("SmokeTests")]
public class WorkDistributionSmokeTests : IClassFixture<DbModeWebApplicationFactory>
{
    private readonly DbModeWebApplicationFactory _factory;

    public WorkDistributionSmokeTests(DbModeWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Verifies that IDispatchOrchestrationService remains registered after deletion of
    /// WorkDistributionRegistration.Kubernetes.cs. Without this registration, PipelineLoopService
    /// falls back to the no-op Legacy path and stops dispatching work items — with no error logged.
    /// </summary>
    [Fact]
    public void IDispatchOrchestrationService_IsRegistered_AfterK8sFileDeleted()
    {
        var service = _factory.Services.GetRequiredService<IDispatchOrchestrationService>();
        Assert.NotNull(service);
    }

    /// <summary>
    /// Verifies that IPendingWorkQuery is still registered after deletion.
    /// ObservableGaugeRegistrationExtensions calls GetRequiredService unconditionally from Program.cs;
    /// a missing registration is a hard startup crash, not a degradation.
    /// </summary>
    [Fact]
    public void IPendingWorkQuery_IsRegistered_AfterK8sFileDeleted()
    {
        var service = _factory.Services.GetRequiredService<IPendingWorkQuery>();
        Assert.NotNull(service);
    }
}
