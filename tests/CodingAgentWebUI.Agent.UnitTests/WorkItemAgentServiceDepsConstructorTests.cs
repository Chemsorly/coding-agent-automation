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

    [Theory]
    [InlineData(nameof(WorkItemAgentServiceDependencies.WorkItemId), "deps.WorkItemId")]
    [InlineData(nameof(WorkItemAgentServiceDependencies.WorkItemClient), "deps.WorkItemClient")]
    [InlineData(nameof(WorkItemAgentServiceDependencies.ConnectionManager), "deps.ConnectionManager")]
    [InlineData(nameof(WorkItemAgentServiceDependencies.WorkItemExecutor), "deps.WorkItemExecutor")]
    [InlineData(nameof(WorkItemAgentServiceDependencies.CompletionReporter), "deps.CompletionReporter")]
    [InlineData(nameof(WorkItemAgentServiceDependencies.Lifetime), "deps.Lifetime")]
    [InlineData(nameof(WorkItemAgentServiceDependencies.Logger), "deps.Logger")]
    public void DepsConstructor_NullRequiredMember_Throws(string memberName, string expectedParamName)
    {
        var deps = memberName switch
        {
            nameof(WorkItemAgentServiceDependencies.WorkItemId) => CreateValidDeps() with { WorkItemId = null! },
            nameof(WorkItemAgentServiceDependencies.WorkItemClient) => CreateValidDeps() with { WorkItemClient = null! },
            nameof(WorkItemAgentServiceDependencies.ConnectionManager) => CreateValidDeps() with { ConnectionManager = null! },
            nameof(WorkItemAgentServiceDependencies.WorkItemExecutor) => CreateValidDeps() with { WorkItemExecutor = null! },
            nameof(WorkItemAgentServiceDependencies.CompletionReporter) => CreateValidDeps() with { CompletionReporter = null! },
            nameof(WorkItemAgentServiceDependencies.Lifetime) => CreateValidDeps() with { Lifetime = null! },
            nameof(WorkItemAgentServiceDependencies.Logger) => CreateValidDeps() with { Logger = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(memberName))
        };
        var act = () => new WorkItemAgentService(deps);
        act.Should().Throw<ArgumentNullException>().WithParameterName(expectedParamName);
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
