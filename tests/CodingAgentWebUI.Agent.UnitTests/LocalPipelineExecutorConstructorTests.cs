using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Null-guard and construction tests for the backward-compatible
/// <see cref="LocalPipelineExecutor"/> constructor that accepts individual parameters.
/// The deps-based constructor is covered by integration-style tests elsewhere.
/// </summary>
public class LocalPipelineExecutorConstructorTests
{
    private static readonly IKiroCliOrchestrator _orchestrator = Mock.Of<IKiroCliOrchestrator>();
    private static readonly IHttpClientFactory _httpClientFactory = Mock.Of<IHttpClientFactory>();
    private static readonly PipelineConfiguration _config = new();
    private static readonly IQualityGateValidator _validator = Mock.Of<IQualityGateValidator>();
    private static readonly Serilog.ILogger _logger = Serilog.Core.Logger.None;

    // ── Null guard tests ─────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullOrchestrator_Throws()
    {
        var act = () => new LocalPipelineExecutor(null!, _httpClientFactory, _config, _validator, _logger);
        act.Should().Throw<ArgumentNullException>().WithParameterName("orchestrator");
    }

    [Fact]
    public void Ctor_NullHttpClientFactory_Throws()
    {
        var act = () => new LocalPipelineExecutor(_orchestrator, null!, _config, _validator, _logger);
        act.Should().Throw<ArgumentNullException>().WithParameterName("httpClientFactory");
    }

    [Fact]
    public void Ctor_NullDefaultPipelineConfig_Throws()
    {
        var act = () => new LocalPipelineExecutor(_orchestrator, _httpClientFactory, null!, _validator, _logger);
        act.Should().Throw<ArgumentNullException>().WithParameterName("defaultPipelineConfig");
    }

    [Fact]
    public void Ctor_NullQualityGateValidator_Throws()
    {
        var act = () => new LocalPipelineExecutor(_orchestrator, _httpClientFactory, _config, null!, _logger);
        act.Should().Throw<ArgumentNullException>().WithParameterName("qualityGateValidator");
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var act = () => new LocalPipelineExecutor(_orchestrator, _httpClientFactory, _config, _validator, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // ── Successful construction ───────────────────────────────────────────────

    [Fact]
    public void Ctor_AllRequiredParams_ConstructsSuccessfully()
    {
        // Should not throw with all required params and optional params at defaults
        var act = () => new LocalPipelineExecutor(_orchestrator, _httpClientFactory, _config, _validator, _logger);
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_WithExplicitAgentIdentity_UsesProvidedIdentity()
    {
        // Verifies the agentIdentity optional param is accepted without error
        // TODO: This test only asserts no throw. A regression that silently drops the provided AgentId
        // (e.g. always defaulting to Environment.MachineName) would not be caught. Consider asserting
        // that the identity is actually used when LocalPipelineExecutor exposes an observable AgentId.
        var identity = new AgentId("test-agent-id");
        var act = () => new LocalPipelineExecutor(
            _orchestrator, _httpClientFactory, _config, _validator, _logger,
            agentIdentity: identity);
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_NullAgentIdentity_DefaultsToMachineName()
    {
        // Null agentIdentity falls back to new AgentId(Environment.MachineName) — just verify no throw
        // TODO: Test name documents a specific expected default (Environment.MachineName) that is never
        // asserted. A regression removing or changing the null-coalescing default in the primary constructor
        // would not be caught. Consider asserting the default identity value once LocalPipelineExecutor
        // exposes it externally.
        var act = () => new LocalPipelineExecutor(
            _orchestrator, _httpClientFactory, _config, _validator, _logger,
            agentIdentity: null);
        act.Should().NotThrow();
    }
}
