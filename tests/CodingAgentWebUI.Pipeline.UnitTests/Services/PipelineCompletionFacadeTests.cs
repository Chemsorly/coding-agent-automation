using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class PipelineCompletionFacadeTests
{
    [Fact]
    public void Constructor_ThrowsOnNullPrOrchestrator()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PipelineCompletionFacade(
                null!,
                new PullRequestFinalizationService(Mock.Of<Serilog.ILogger>()),
                new FeedbackService(Mock.Of<Serilog.ILogger>()),
                Mock.Of<IPipelineRunHistoryService>()));
    }

    [Fact]
    public void Constructor_ThrowsOnNullFinalization()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PipelineCompletionFacade(
                new PullRequestOrchestrator(Mock.Of<Serilog.ILogger>()),
                null!,
                new FeedbackService(Mock.Of<Serilog.ILogger>()),
                Mock.Of<IPipelineRunHistoryService>()));
    }

    [Fact]
    public void Constructor_ThrowsOnNullFeedbackService()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PipelineCompletionFacade(
                new PullRequestOrchestrator(Mock.Of<Serilog.ILogger>()),
                new PullRequestFinalizationService(Mock.Of<Serilog.ILogger>()),
                null!,
                Mock.Of<IPipelineRunHistoryService>()));
    }

    [Fact]
    public void Constructor_ThrowsOnNullHistoryService()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PipelineCompletionFacade(
                new PullRequestOrchestrator(Mock.Of<Serilog.ILogger>()),
                new PullRequestFinalizationService(Mock.Of<Serilog.ILogger>()),
                new FeedbackService(Mock.Of<Serilog.ILogger>()),
                null!));
    }

    [Fact]
    // TODO: This test is tautological — it asserts auto-properties return constructor-assigned values,
    // which is guaranteed by C# compiler behavior. Consider replacing with a behavioral test.
    public void Properties_ExposeInjectedDependencies()
    {
        var prOrchestrator = new PullRequestOrchestrator(Mock.Of<Serilog.ILogger>());
        var finalization = new PullRequestFinalizationService(Mock.Of<Serilog.ILogger>());
        var feedback = new FeedbackService(Mock.Of<Serilog.ILogger>());
        var history = Mock.Of<IPipelineRunHistoryService>();

        var facade = new PipelineCompletionFacade(prOrchestrator, finalization, feedback, history);

        Assert.Same(prOrchestrator, facade.PrOrchestrator);
        Assert.Same(finalization, facade.Finalization);
        Assert.Same(feedback, facade.FeedbackService);
        Assert.Same(history, facade.HistoryService);
    }
}
