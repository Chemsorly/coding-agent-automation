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
    /// IDispatchOrchestrationService must resolve — the drawer services constructor-inject it.
    ///
    /// The previous version of this test was named <c>..._IsNotRegistered_AfterSpec045Task8</c>
    /// and asserted <c>NotNull</c>, which contradicted its own name and could not fail: the test
    /// factory substitutes a mock, so the assertion said nothing about production wiring. The
    /// production container does register it (ServiceCollectionExtensions.PipelineBackgroundServices).
    /// </summary>
    [Fact]
    public void IDispatchOrchestrationService_Resolves()
    {
        var service = _factory.Services.GetService<IDispatchOrchestrationService>();
        Assert.NotNull(service);
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

    /// <summary>
    /// Verifies that IPendingWorkQuery is NOT registered after Spec 045 Req 1.2 (M1 gauge audit).
    /// dispatch.queue.depth was backed by DbPendingWorkQuery and removed.
    /// </summary>
    [Fact]
    public void IPendingWorkQuery_IsNotRegistered_AfterSpec045Req12()
    {
        // IPendingWorkQuery was removed in Spec 045 Req 1.2 (M1 gauge audit) because
        // dispatch.queue.depth backed by IDbContextFactory. No PrometheusRule references this metric.
        var service = _factory.Services.GetService<IPendingWorkQuery>();
        Assert.Null(service);
    }
}
