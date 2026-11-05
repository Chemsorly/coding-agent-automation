using AwesomeAssertions;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Services;
using Xunit;

namespace CodingAgent.JobController.UnitTests.Dispatch;

/// <summary>Spec 057 req 4.1: the chat refusal texts.</summary>
public sealed class AgentStartTextsTests
{
    // req 4.1
    [Fact]
    public void ChatRefusal_ForATemplateLimit_ShouldNameTheTemplate()
    {
        var decision = new AgentStartDecision(false, LimitScope.Template, LimitSource.Helm, 4, 4);

        AgentStartTexts.ChatRefusal(decision, "dotnet,dotnet10,kiro").Should().Be(
            "4 of 4 agents are running for dotnet,dotnet10,kiro (limit set in the Helm chart). Try again when one finishes.");
    }

    // req 4.1
    [Fact]
    public void ChatRefusal_ForTheGlobalLimit_ShouldNotNameATemplate()
    {
        var decision = new AgentStartDecision(false, LimitScope.Global, LimitSource.Helm, 6, 6);

        AgentStartTexts.ChatRefusal(decision, "dotnet,dotnet10,kiro").Should().Be(
            "6 of 6 agents are running (limit set in the Helm chart). Try again when one finishes.");
    }
}
