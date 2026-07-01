using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class PipelineCancellationFacadeTests
{
    [Fact]
    public void Constructor_AcceptsNullDedupGuard()
    {
        var facade = new PipelineCancellationFacade(null, Mock.Of<IAgentCancellationSender>());

        Assert.Null(facade.DedupGuard);
    }

    [Fact]
    public void Constructor_AcceptsNullAgentCancellation()
    {
        var facade = new PipelineCancellationFacade(Mock.Of<IJobDeduplicationGuard>(), null);

        Assert.Null(facade.AgentCancellation);
    }

    [Fact]
    public void Constructor_AcceptsBothNull()
    {
        var facade = new PipelineCancellationFacade(null, null);

        Assert.Null(facade.DedupGuard);
        Assert.Null(facade.AgentCancellation);
    }

    [Fact]
    // TODO: This test is tautological — it asserts auto-properties return constructor-assigned values,
    // which is guaranteed by C# compiler behavior. Consider replacing with a behavioral test.
    public void Properties_ExposeInjectedDependencies()
    {
        var dedupGuard = Mock.Of<IJobDeduplicationGuard>();
        var agentCancellation = Mock.Of<IAgentCancellationSender>();

        var facade = new PipelineCancellationFacade(dedupGuard, agentCancellation);

        Assert.Same(dedupGuard, facade.DedupGuard);
        Assert.Same(agentCancellation, facade.AgentCancellation);
    }
}
