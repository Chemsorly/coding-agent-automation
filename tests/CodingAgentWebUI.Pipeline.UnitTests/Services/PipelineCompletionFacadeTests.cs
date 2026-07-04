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

}
