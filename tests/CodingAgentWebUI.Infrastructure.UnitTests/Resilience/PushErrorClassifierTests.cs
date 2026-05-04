using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Resilience;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Resilience;

public class PushErrorClassifierTests
{
    [Theory]
    [InlineData("Push failed: 401 Unauthorized")]
    [InlineData("authentication required")]
    [InlineData("invalid credentials")]
    [InlineData("403 Forbidden")]
    public void Classify_AuthFailure_ReturnsAuth(string message)
    {
        PushErrorClassifier.Classify(message).Should().Be(PushErrorClassifier.PushFailureCategory.Auth);
    }

    [Theory]
    [InlineData("protected branch hook declined")]
    [InlineData("required status check 'build' is failing")]
    public void Classify_BranchProtection_ReturnsBranchProtection(string message)
    {
        PushErrorClassifier.Classify(message).Should().Be(PushErrorClassifier.PushFailureCategory.BranchProtection);
    }

    [Theory]
    [InlineData("non-fast-forward update rejected")]
    [InlineData("push rejected by remote")]
    public void Classify_Conflict_ReturnsConflict(string message)
    {
        PushErrorClassifier.Classify(message).Should().Be(PushErrorClassifier.PushFailureCategory.Conflict);
    }

    [Theory]
    [InlineData("connection timed out")]
    [InlineData("DNS resolution failed")]
    [InlineData("connection reset by peer")]
    [InlineData("503 Service Unavailable")]
    [InlineData("network is unreachable")]
    [InlineData("Name or service not known (api.github.com:443)")]
    [InlineData("could not resolve host")]
    public void Classify_Network_ReturnsNetwork(string message)
    {
        PushErrorClassifier.Classify(message).Should().Be(PushErrorClassifier.PushFailureCategory.Network);
    }

    [Theory]
    [InlineData("some unknown error")]
    [InlineData("unexpected failure")]
    public void Classify_Unknown_ReturnsUnknown(string message)
    {
        PushErrorClassifier.Classify(message).Should().Be(PushErrorClassifier.PushFailureCategory.Unknown);
    }

    [Fact]
    public void GetActionableMessage_Auth_ContainsTokenExpired()
    {
        var msg = PushErrorClassifier.GetActionableMessage(PushErrorClassifier.PushFailureCategory.Auth);
        msg.Should().Contain("authentication error");
    }

    [Fact]
    public void GetActionableMessage_BranchProtection_ContainsBranchName()
    {
        var msg = PushErrorClassifier.GetActionableMessage(PushErrorClassifier.PushFailureCategory.BranchProtection, "main");
        msg.Should().Contain("main").And.Contain("protected");
    }

    [Fact]
    public void GetActionableMessage_Network_ContainsRetries()
    {
        var msg = PushErrorClassifier.GetActionableMessage(PushErrorClassifier.PushFailureCategory.Network);
        msg.Should().Contain("network error");
    }

    [Fact]
    public void GetActionableMessage_Conflict_ContainsDiverged()
    {
        var msg = PushErrorClassifier.GetActionableMessage(PushErrorClassifier.PushFailureCategory.Conflict);
        msg.Should().Contain("diverged");
    }

    [Fact]
    public void Classify_NullMessage_ThrowsArgumentNullException()
    {
        var act = () => PushErrorClassifier.Classify(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
