using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.IntegrationTests.Smoke;

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
    /// Spec 051 2.3c: manual dispatch runs in the Pipeline API, so the web host no longer registers
    /// IDispatchOrchestrationService. The test factories stopped substituting a mock for it, so this
    /// checks the production wiring.
    /// </summary>
    [Fact]
    public void IDispatchOrchestrationService_IsNotRegistered()
    {
        var service = _factory.Services.GetService<IDispatchOrchestrationService>();
        Assert.Null(service);
    }

    /// <summary>
    /// IChatJobDispatcher must resolve — <c>AgentChat.razor</c> declares
    /// <c>@inject IChatJobDispatcher</c>, and a Razor injection is not covered by
    /// <c>ValidateOnBuild</c>, so a missing registration only surfaces when a user opens the page.
    ///
    /// Spec 043 deleted WorkDistributionRegistration.Kubernetes.cs — which held this registration —
    /// expecting Spec 044 to re-home it in the API. That move never happened, leaving the
    /// interface registered nowhere and the chat page throwing on first render.
    /// </summary>
    [Fact]
    public void IChatJobDispatcher_Resolves()
    {
        var service = _factory.Services.GetService<IChatJobDispatcher>();
        Assert.NotNull(service);
    }
}
