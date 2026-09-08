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

    [Theory]
    [InlineData(nameof(LocalPipelineExecutorDependencies.Orchestrator), "deps.Orchestrator")]
    [InlineData(nameof(LocalPipelineExecutorDependencies.HttpClientFactory), "deps.HttpClientFactory")]
    [InlineData(nameof(LocalPipelineExecutorDependencies.DefaultPipelineConfig), "deps.DefaultPipelineConfig")]
    [InlineData(nameof(LocalPipelineExecutorDependencies.QualityGateValidator), "deps.QualityGateValidator")]
    [InlineData(nameof(LocalPipelineExecutorDependencies.Logger), "deps.Logger")]
    public void DepsConstructor_NullRequiredMember_Throws(string memberName, string expectedParamName)
    {
        var deps = memberName switch
        {
            nameof(LocalPipelineExecutorDependencies.Orchestrator) => CreateValidDeps() with { Orchestrator = null! },
            nameof(LocalPipelineExecutorDependencies.HttpClientFactory) => CreateValidDeps() with { HttpClientFactory = null! },
            nameof(LocalPipelineExecutorDependencies.DefaultPipelineConfig) => CreateValidDeps() with { DefaultPipelineConfig = null! },
            nameof(LocalPipelineExecutorDependencies.QualityGateValidator) => CreateValidDeps() with { QualityGateValidator = null! },
            nameof(LocalPipelineExecutorDependencies.Logger) => CreateValidDeps() with { Logger = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(memberName))
        };
        var act = () => new LocalPipelineExecutor(deps);
        act.Should().Throw<ArgumentNullException>().WithParameterName(expectedParamName);
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
        // OpenIssueContextWriter defaults to new OpenIssueContextWriter(logger) — must not throw.
        // TODO: Assert the concrete default type once LocalPipelineExecutor exposes it externally.
        var deps = CreateValidDeps(openIssueContextWriter: null);
        var act = () => new LocalPipelineExecutor(deps);
        act.Should().NotThrow();
    }

    [Fact]
    public void DepsConstructor_NullAgentIdentity_DefaultsToMachineName()
    {
        // AgentIdentity defaults to new AgentId(Environment.MachineName) — must not throw.
        // TODO: Assert the fallback value once LocalPipelineExecutor exposes AgentId externally.
        var deps = CreateValidDeps(agentIdentity: null);
        var act = () => new LocalPipelineExecutor(deps);
        act.Should().NotThrow();
    }

    [Fact]
    public void DepsConstructor_NullReporterFactory_DefaultsToConcrete()
    {
        // ReporterFactory defaults to new PipelineReporterFactory(logger) — must not throw.
        // TODO: Assert the concrete default type once LocalPipelineExecutor exposes it externally.
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
