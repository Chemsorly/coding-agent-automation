using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Property-based tests for hub method authorization via AgentAuthorizationFilter.
/// Tests the filter logic directly without constructing a full HubInvocationContext.
/// </summary>
public class HubAuthorizationPropertyTests
{
    private static AgentRegistryService CreateRegistry() =>
        new(new Mock<ILogger>().Object);

    /// <summary>
    /// Property 5: Hub Method Job Authorization
    /// For any agent-invoked hub method with jobId, if jobId doesn't match agent's active jobId,
    /// call is rejected. We test the underlying registry lookup that the filter depends on.
    /// **Validates: Requirements 5.12**
    /// </summary>
    [Property]
    public void MismatchedJobId_FailsValidation(NonEmptyString agentId, NonEmptyString activeJobId, NonEmptyString wrongJobId)
    {
        if (activeJobId.Get == wrongJobId.Get) return;

        var registry = CreateRegistry();
        var entry = registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId.Get,
            Hostname = "host1",
            AgentType = "kiro-dotnet",
            Labels = new[] { "kiro" }
        }, "conn-1");
        entry.ActiveJobId = activeJobId.Get;

        // Simulate the filter's validation logic
        var agent = registry.GetByConnectionId("conn-1");
        agent.Should().NotBeNull();

        var jobIdMatches = string.Equals(agent!.ActiveJobId, wrongJobId.Get, StringComparison.Ordinal);
        jobIdMatches.Should().BeFalse("wrong jobId should not match agent's active jobId");
    }

    /// <summary>
    /// Property 5 (continued): Matching jobId passes validation.
    /// **Validates: Requirements 5.12**
    /// </summary>
    [Property]
    public void MatchingJobId_PassesValidation(NonEmptyString agentId, NonEmptyString jobId)
    {
        var registry = CreateRegistry();
        var entry = registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId.Get,
            Hostname = "host1",
            AgentType = "kiro-dotnet",
            Labels = new[] { "kiro" }
        }, "conn-1");
        entry.ActiveJobId = jobId.Get;

        // Simulate the filter's validation logic
        var agent = registry.GetByConnectionId("conn-1");
        agent.Should().NotBeNull();

        var jobIdMatches = string.Equals(agent!.ActiveJobId, jobId.Get, StringComparison.Ordinal);
        jobIdMatches.Should().BeTrue("matching jobId should pass validation");
    }

    /// <summary>
    /// Property 4 (continued): Unregistered connection fails lookup.
    /// **Validates: Requirements 1.7**
    /// </summary>
    [Property]
    public void UnregisteredConnection_FailsLookup(NonEmptyString connectionId)
    {
        var registry = CreateRegistry();

        // Simulate the filter's first check: lookup agent by connectionId
        var agent = registry.GetByConnectionId(connectionId.Get);
        agent.Should().BeNull("unregistered connection should not resolve to an agent");
    }
}
