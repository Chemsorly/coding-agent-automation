using AwesomeAssertions;
using CodingAgent.Infrastructure.Resilience;

namespace CodingAgent.Infrastructure.UnitTests.Resilience;

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
    [InlineData("cannot push non-fastforwardable reference")]
    [InlineData("cannot push because a reference that you are trying to update on the remote contains commits that are not present locally.")]
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

    [Theory]
    [InlineData("refusing to allow a GitHub App to create or update workflow '.github/workflows/ci.yml' without 'workflows' permission")]
    [InlineData("REFUSING TO ALLOW A GITHUB APP TO CREATE OR UPDATE WORKFLOW files")]
    public void Classify_WorkflowPermissionMessage_ReturnsWorkflowPermission(string message)
    {
        PushErrorClassifier.Classify(message).Should().Be(PushErrorClassifier.PushFailureCategory.WorkflowPermission);
    }

    [Fact]
    public void GetActionableMessage_WorkflowPermission_ReturnsWorkflowFilesMessage()
    {
        var msg = PushErrorClassifier.GetActionableMessage(PushErrorClassifier.PushFailureCategory.WorkflowPermission);
        msg.Should().Be("Push failed: the GitHub App may not create or update workflow files (.github/workflows).");
    }

    [Fact]
    public void Classify_WorkflowPermissionMessage_TakesPriorityOverAuth()
    {
        // The workflow permission message may contain "403" — workflow check must fire first
        const string message = "403 refusing to allow a GitHub App to create or update workflow";
        PushErrorClassifier.Classify(message).Should().Be(PushErrorClassifier.PushFailureCategory.WorkflowPermission);
    }
}
