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

}
