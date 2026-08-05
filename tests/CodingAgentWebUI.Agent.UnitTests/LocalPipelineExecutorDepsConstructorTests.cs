using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for the <see cref="LocalPipelineExecutor(LocalPipelineExecutorDependencies)"/>
/// deps-object constructor (new code introduced in this PR).
/// The existing tests cover the parameter-list constructor; these cover the new overload.
/// </summary>
public class LocalPipelineExecutorDepsConstructorTests
{
    private static LocalPipelineExecutorDependencies CreateValidDeps(
        IBrainUpdateService? brainUpdateService = null,
        IPipelineRunHistoryService? historyService = null,
        IOpenIssueContextWriter? openIssueContextWriter = null,
        AgentId? agentIdentity = null,
        IPipelineReporterFactory? reporterFactory = null) =>
        new(
            Orchestrator: Mock.Of<IKiroCliOrchestrator>(),
            HttpClientFactory: Mock.Of<IHttpClientFactory>(),
            DefaultPipelineConfig: new PipelineConfiguration(),
            QualityGateValidator: Mock.Of<IQualityGateValidator>(),
            Logger: Mock.Of<Serilog.ILogger>(),
            BrainUpdateService: brainUpdateService,
            HistoryService: historyService,
            OpenIssueContextWriter: openIssueContextWriter,
            AgentIdentity: agentIdentity,
            ReporterFactory: reporterFactory);

    // ── Null guard — deps object itself ──────────────────────────────────

    [Fact]
    public void DepsConstructor_NullDeps_ThrowsArgumentNullException()
    {
        var act = () => new LocalPipelineExecutor((LocalPipelineExecutorDependencies)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Null guards — required members ──────────────────────────────────

    [Fact]
    public void DepsConstructor_NullOrchestrator_Throws()
    {
        var deps = CreateValidDeps() with { Orchestrator = null! };
        var act = () => new LocalPipelineExecutor(deps);
        act.Should().Throw<ArgumentNullException>().WithParameterName("deps.Orchestrator");
    }

    [Fact]
    public void DepsConstructor_NullHttpClientFactory_Throws()
    {
        var deps = CreateValidDeps() with { HttpClientFactory = null! };
        var act = () => new LocalPipelineExecutor(deps);
        act.Should().Throw<ArgumentNullException>().WithParameterName("deps.HttpClientFactory");
    }

    [Fact]
    public void DepsConstructor_NullDefaultPipelineConfig_Throws()
    {
        var deps = CreateValidDeps() with { DefaultPipelineConfig = null! };
        var act = () => new LocalPipelineExecutor(deps);
        act.Should().Throw<ArgumentNullException>().WithParameterName("deps.DefaultPipelineConfig");
    }

    [Fact]
    public void DepsConstructor_NullQualityGateValidator_Throws()
    {
        var deps = CreateValidDeps() with { QualityGateValidator = null! };
        var act = () => new LocalPipelineExecutor(deps);
        act.Should().Throw<ArgumentNullException>().WithParameterName("deps.QualityGateValidator");
    }

    [Fact]
    public void DepsConstructor_NullLogger_Throws()
    {
        var deps = CreateValidDeps() with { Logger = null! };
        var act = () => new LocalPipelineExecutor(deps);
        act.Should().Throw<ArgumentNullException>().WithParameterName("deps.Logger");
    }

    // ── Valid construction ───────────────────────────────────────────────

    [Fact]
    public void DepsConstructor_AllRequired_DoesNotThrow()
    {
        var deps = CreateValidDeps();
        var act = () => new LocalPipelineExecutor(deps);
        act.Should().NotThrow();
    }

    [Fact]
    public void DepsConstructor_WithAllOptionals_DoesNotThrow()
    {
        var deps = CreateValidDeps(
            brainUpdateService: Mock.Of<IBrainUpdateService>(),
            historyService: Mock.Of<IPipelineRunHistoryService>(),
            openIssueContextWriter: Mock.Of<IOpenIssueContextWriter>(),
            agentIdentity: new AgentId("custom-agent"),
            reporterFactory: Mock.Of<IPipelineReporterFactory>());

        var act = () => new LocalPipelineExecutor(deps);
        act.Should().NotThrow();
    }

    [Fact]
    public void DepsConstructor_NullOpenIssueContextWriter_DefaultsToConcreteImpl()
    {
        // OpenIssueContextWriter defaults to new OpenIssueContextWriter(logger) — must not throw
        var deps = CreateValidDeps(openIssueContextWriter: null);
        var act = () => new LocalPipelineExecutor(deps);
        act.Should().NotThrow();
    }

    [Fact]
    public void DepsConstructor_NullAgentIdentity_DefaultsToMachineName()
    {
        // AgentIdentity defaults to new AgentId(Environment.MachineName) — must not throw
        var deps = CreateValidDeps(agentIdentity: null);
        var act = () => new LocalPipelineExecutor(deps);
        act.Should().NotThrow();
    }

    [Fact]
    public void DepsConstructor_NullReporterFactory_DefaultsToConcrete()
    {
        // ReporterFactory defaults to new PipelineReporterFactory(logger) — must not throw
        var deps = CreateValidDeps(reporterFactory: null);
        var act = () => new LocalPipelineExecutor(deps);
        act.Should().NotThrow();
    }

    // ── Implements IPipelineExecutor ─────────────────────────────────────

    [Fact]
    public void DepsConstructor_ResultImplementsIPipelineExecutor()
    {
        var deps = CreateValidDeps();
        var executor = new LocalPipelineExecutor(deps);
        executor.Should().BeAssignableTo<IPipelineExecutor>();
    }
}
