using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// <see cref="AgentRequest.AppliesLightTier"/>: the route's light tier is for a call that starts a fresh session. A
/// resumed session never changes its model or effort because of it.
/// </summary>
public class AgentRequestTests
{
    private static AgentRequest Request(bool useLightTier, bool useResume = false, string? resumeSessionId = null) => new()
    {
        Prompt = "p",
        WorkspacePath = "/ws",
        UseLightTier = useLightTier,
        UseResume = useResume,
        ResumeSessionId = resumeSessionId,
    };

    [Fact]
    public void UseLightTier_IsOffByDefault()
    {
        var request = new AgentRequest { Prompt = "p", WorkspacePath = "/ws" };

        request.UseLightTier.Should().BeFalse();
        request.AppliesLightTier.Should().BeFalse();
    }

    [Fact]
    public void AppliesLightTier_FreshSessionThatAsksForIt_IsTrue()
        => Request(useLightTier: true).AppliesLightTier.Should().BeTrue();

    [Fact]
    public void AppliesLightTier_NotAskedFor_IsFalse()
        => Request(useLightTier: false).AppliesLightTier.Should().BeFalse();

    [Fact]
    public void AppliesLightTier_ResumeOfTheMainConversation_IsFalse()
        => Request(useLightTier: true, useResume: true).AppliesLightTier.Should().BeFalse();

    [Fact]
    public void AppliesLightTier_ResumeOfAGivenSession_IsFalse()
        => Request(useLightTier: true, resumeSessionId: "session-1").AppliesLightTier.Should().BeFalse();

    [Fact]
    public void AppliesLightTier_AnEmptyResumeSessionId_IsFalse_BecauseItIsNotNull()
        => Request(useLightTier: true, resumeSessionId: "").AppliesLightTier.Should().BeFalse();
}
