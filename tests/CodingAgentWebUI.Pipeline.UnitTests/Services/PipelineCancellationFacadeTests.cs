using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class PipelineCancellationFacadeTests
{
    [Fact]
    public void Constructor_AcceptsAgentCancellation()
    {
        var sender = Mock.Of<IAgentCancellationSender>();

        var facade = new PipelineCancellationFacade(sender);

        Assert.Same(sender, facade.AgentCancellation);
    }

    [Fact]
    public void Constructor_AcceptsNullAgentCancellation()
    {
        var facade = new PipelineCancellationFacade(null);

        Assert.Null(facade.AgentCancellation);
    }
}
