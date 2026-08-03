using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for the <see cref="WorkItemAgentService(WorkItemAgentServiceDependencies)"/>
/// deps-object constructor (new code introduced in this PR).
/// </summary>
public class WorkItemAgentServiceDepsConstructorTests
{
    private readonly Mock<Serilog.ILogger> _logger = new();
    private readonly Mock<IHostApplicationLifetime> _lifetime = new();

    private WorkItemAgentServiceDependencies CreateValidDeps(IServiceProvider? serviceProvider = null) =>
        new(
            WorkItemId: "wi-test",
            WorkItemClient: Mock.Of<IWorkItemLifecycleClient>(),
            ConnectionManager: Mock.Of<IAgentConnectionManager>(),
            WorkItemExecutor: Mock.Of<IWorkItemExecutor>(),
            CompletionReporter: Mock.Of<IJobCompletionReporter>(),
            AgentId: new AgentId("test-agent"),
            Lifetime: _lifetime.Object,
            Logger: _logger.Object,
            ServiceProvider: serviceProvider);

    // ── Null guard — deps object itself ──────────────────────────────────

    [Fact]
    public void DepsConstructor_NullDeps_ThrowsArgumentNullException()
    {
        var act = () => new WorkItemAgentService((WorkItemAgentServiceDependencies)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Null guards — required members ──────────────────────────────────

    [Fact]
    public void DepsConstructor_NullWorkItemId_Throws()
    {
        var deps = CreateValidDeps() with { WorkItemId = null! };
        var act = () => new WorkItemAgentService(deps);
        act.Should().Throw<ArgumentNullException>().WithParameterName("WorkItemId");
    }

    [Fact]
    public void DepsConstructor_NullWorkItemClient_Throws()
    {
        var deps = CreateValidDeps() with { WorkItemClient = null! };
        var act = () => new WorkItemAgentService(deps);
        act.Should().Throw<ArgumentNullException>().WithParameterName("WorkItemClient");
    }

    [Fact]
    public void DepsConstructor_NullConnectionManager_Throws()
    {
        var deps = CreateValidDeps() with { ConnectionManager = null! };
        var act = () => new WorkItemAgentService(deps);
        act.Should().Throw<ArgumentNullException>().WithParameterName("ConnectionManager");
    }

    [Fact]
    public void DepsConstructor_NullWorkItemExecutor_Throws()
    {
        var deps = CreateValidDeps() with { WorkItemExecutor = null! };
        var act = () => new WorkItemAgentService(deps);
        act.Should().Throw<ArgumentNullException>().WithParameterName("WorkItemExecutor");
    }

    [Fact]
    public void DepsConstructor_NullCompletionReporter_Throws()
    {
        var deps = CreateValidDeps() with { CompletionReporter = null! };
        var act = () => new WorkItemAgentService(deps);
        act.Should().Throw<ArgumentNullException>().WithParameterName("CompletionReporter");
    }

    [Fact]
    public void DepsConstructor_NullLifetime_Throws()
    {
        var deps = CreateValidDeps() with { Lifetime = null! };
        var act = () => new WorkItemAgentService(deps);
        act.Should().Throw<ArgumentNullException>().WithParameterName("Lifetime");
    }

    [Fact]
    public void DepsConstructor_NullLogger_Throws()
    {
        var deps = CreateValidDeps() with { Logger = null! };
        var act = () => new WorkItemAgentService(deps);
        act.Should().Throw<ArgumentNullException>().WithParameterName("Logger");
    }

    // ── Valid construction ───────────────────────────────────────────────

    [Fact]
    public void DepsConstructor_AllRequired_DoesNotThrow()
    {
        var deps = CreateValidDeps();
        var act = () => new WorkItemAgentService(deps);
        act.Should().NotThrow();
    }

    [Fact]
    public void DepsConstructor_WithServiceProvider_DoesNotThrow()
    {
        var sp = Mock.Of<IServiceProvider>();
        var deps = CreateValidDeps(serviceProvider: sp);
        var act = () => new WorkItemAgentService(deps);
        act.Should().NotThrow();
    }

    [Fact]
    public void DepsConstructor_NullServiceProvider_DoesNotThrow()
    {
        var deps = CreateValidDeps(serviceProvider: null);
        var act = () => new WorkItemAgentService(deps);
        act.Should().NotThrow();
    }

    // ── Observable properties ─────────────────────────────────────────────

    [Fact]
    public void DepsConstructor_IsBusy_InitiallyFalse()
    {
        var service = new WorkItemAgentService(CreateValidDeps());
        service.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void DepsConstructor_CurrentStep_InitiallyNull()
    {
        var service = new WorkItemAgentService(CreateValidDeps());
        service.CurrentStep.Should().BeNull();
    }

    [Fact]
    public void DepsConstructor_IsConnected_DelegatesToConnectionManager()
    {
        var mockConnMgr = new Mock<IAgentConnectionManager>();
        mockConnMgr.Setup(m => m.IsConnected).Returns(true);

        var deps = CreateValidDeps() with { ConnectionManager = mockConnMgr.Object };
        var service = new WorkItemAgentService(deps);

        service.IsConnected.Should().BeTrue();
    }

    [Fact]
    public void DepsConstructor_CancelCurrentJob_DoesNotThrow()
    {
        var service = new WorkItemAgentService(CreateValidDeps());
        var act = () => service.CancelCurrentJob();
        act.Should().NotThrow();
    }
}
