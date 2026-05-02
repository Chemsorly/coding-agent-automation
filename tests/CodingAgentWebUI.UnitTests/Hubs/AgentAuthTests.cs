using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.Pipeline.Interfaces;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Unit tests for AgentAuthorizationFilter and RequiresActiveJobAttribute.
/// Tests the authorization logic at the registry level since the filter
/// requires a full SignalR pipeline to invoke.
/// </summary>
public class AgentAuthTests
{
    private readonly AgentRegistryService _registry;
    private readonly Mock<ILogger> _mockLogger;

    public AgentAuthTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
    }

    // ── Constructor validation ──────────────────────────────────────────

    [Fact]
    public void AgentAuthorizationFilter_NullRegistry_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AgentAuthorizationFilter(null!, _mockLogger.Object));
    }

    [Fact]
    public void AgentAuthorizationFilter_NullLogger_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AgentAuthorizationFilter(_registry, null!));
    }

    [Fact]
    public void AgentAuthorizationFilter_ValidArgs_CreatesInstance()
    {
        var filter = new AgentAuthorizationFilter(_registry, _mockLogger.Object);
        filter.Should().NotBeNull();
    }

    // ── RequiresActiveJobAttribute ──────────────────────────────────────

    [Fact]
    public void RequiresActiveJobAttribute_CanBeInstantiated()
    {
        var attr = new RequiresActiveJobAttribute();
        attr.Should().NotBeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_OnJobAccepted()
    {
        var method = typeof(AgentHub).GetMethod("JobAccepted");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_OnReportJobCompleted()
    {
        var method = typeof(AgentHub).GetMethod("ReportJobCompleted");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_OnReportStepTransition()
    {
        var method = typeof(AgentHub).GetMethod("ReportStepTransition");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_OnReportBrainSyncResult()
    {
        var method = typeof(AgentHub).GetMethod("ReportBrainSyncResult");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_OnReportOutputLines()
    {
        var method = typeof(AgentHub).GetMethod("ReportOutputLines");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_OnReportChatEntry()
    {
        var method = typeof(AgentHub).GetMethod("ReportChatEntry");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_OnReportQualityGateResult()
    {
        var method = typeof(AgentHub).GetMethod("ReportQualityGateResult");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_OnRequestPostComment()
    {
        var method = typeof(AgentHub).GetMethod("RequestPostComment");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_OnRequestLabelChange()
    {
        var method = typeof(AgentHub).GetMethod("RequestLabelChange");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_OnRequestTokenRefresh()
    {
        var method = typeof(AgentHub).GetMethod("RequestTokenRefresh");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_NotOnRegisterAgent()
    {
        var method = typeof(AgentHub).GetMethod("RegisterAgent");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().BeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_NotOnDeregisterAgent()
    {
        var method = typeof(AgentHub).GetMethod("DeregisterAgent");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().BeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_NotOnHeartbeat()
    {
        var method = typeof(AgentHub).GetMethod("Heartbeat");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().BeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_NotOnAgentReady()
    {
        var method = typeof(AgentHub).GetMethod("AgentReady");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().BeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_NotOnJobRejected()
    {
        var method = typeof(AgentHub).GetMethod("JobRejected");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().BeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_NotOnReportChatResponse()
    {
        var method = typeof(AgentHub).GetMethod("ReportChatResponse");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().BeNull();
    }

    [Fact]
    public void RequiresActiveJobAttribute_NotOnReportChatCompleted()
    {
        var method = typeof(AgentHub).GetMethod("ReportChatCompleted");
        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequiresActiveJobAttribute>().Should().BeNull();
    }

    // ── Authorization logic (registry-level validation) ─────────────────

    [Fact]
    public void UnregisteredConnection_NotFoundInRegistry()
    {
        _registry.GetByConnectionId("unknown-conn").Should().BeNull();
    }

    [Fact]
    public void RegisteredAgent_FoundByConnectionId()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1", Hostname = "h", AgentType = "t", Labels = new[] { "l" }
        }, "conn-1");

        var agent = _registry.GetByConnectionId("conn-1");
        agent.Should().NotBeNull();
        agent!.AgentId.Should().Be("agent-1");
    }

    [Fact]
    public void ActiveJobId_Mismatch_Detectable()
    {
        var entry = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1", Hostname = "h", AgentType = "t", Labels = new[] { "l" }
        }, "conn-1");
        entry.ActiveJobId = "job-1";

        var agent = _registry.GetByConnectionId("conn-1");
        string.Equals(agent!.ActiveJobId, "job-2", StringComparison.Ordinal).Should().BeFalse();
    }

    [Fact]
    public void ActiveJobId_Match_Detectable()
    {
        var entry = _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1", Hostname = "h", AgentType = "t", Labels = new[] { "l" }
        }, "conn-1");
        entry.ActiveJobId = "job-1";

        var agent = _registry.GetByConnectionId("conn-1");
        string.Equals(agent!.ActiveJobId, "job-1", StringComparison.Ordinal).Should().BeTrue();
    }

    [Fact]
    public void AgentWithNoActiveJob_HasNullActiveJobId()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1", Hostname = "h", AgentType = "t", Labels = new[] { "l" }
        }, "conn-1");

        var agent = _registry.GetByConnectionId("conn-1");
        agent!.ActiveJobId.Should().BeNull();
    }

    // ── IAgentHub interface ─────────────────────────────────────────────

    [Fact]
    public void AgentHub_ImplementsIAgentHub()
    {
        typeof(AgentHub).GetInterfaces().Should().Contain(typeof(IAgentHub));
    }

    [Fact]
    public void AgentHub_IsSealed_NotInheritable()
    {
        typeof(AgentHub).IsSealed.Should().BeTrue();
    }

    // ── AgentApiKeyDefaults ─────────────────────────────────────────────

    [Fact]
    public void AgentApiKeyDefaults_AuthenticationScheme_HasExpectedValue()
    {
        AgentApiKeyDefaults.AuthenticationScheme.Should().Be("AgentApiKey");
    }
}
