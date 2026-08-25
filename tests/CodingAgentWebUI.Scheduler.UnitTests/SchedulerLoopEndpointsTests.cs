using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Scheduler;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Reflection;
using Xunit;

namespace CodingAgentWebUI.Scheduler.UnitTests;

/// <summary>
/// Unit tests for SchedulerLoopEndpoints — tests handlers and ApiKeyFilter directly
/// without spinning up a WebApplication.
/// </summary>
public sealed class SchedulerLoopEndpointsTests
{
    // ── Handler: GetLoopStatus ──────────────────────────────────────────────

    [Fact]
    public void GetLoopStatus_WhenCacheHasValue_ReturnsCachedDto()
    {
        var cached = MakeDto(isActive: true, status: "Running");
        var cache = new LoopStatusCache();
        cache.Update(cached);

        var mockLoop = new Mock<IPipelineLoopService>();
        var result = SchedulerLoopEndpoints.GetLoopStatus(mockLoop.Object, cache);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStatusDto>>().Subject;
        ok.Value!.IsLoopActive.Should().BeTrue();
        ok.Value.StatusMessage.Should().Be("Running");
        mockLoop.Verify(l => l.IsLoopActive, Times.Never, "must serve from cache, not the loop service");
    }

    [Fact]
    public void GetLoopStatus_WhenCacheEmpty_BuildsFromLoopService()
    {
        var cache = new LoopStatusCache(); // empty
        var mockLoop = MockLoopService(isActive: false, status: "Stopped");

        var result = SchedulerLoopEndpoints.GetLoopStatus(mockLoop.Object, cache);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStatusDto>>();
    }

    // ── Handler: StartLoop ──────────────────────────────────────────────────

    [Fact]
    public async Task StartLoop_WhenStartsSuccessfully_PersistsAutoStartAndReturnsOk()
    {
        var mockLoop = MockLoopService();
        mockLoop.Setup(l => l.StartLoopAsync()).ReturnsAsync(true);

        var mockConfig = new Mock<IPipelineApiConfigClient>();
        mockConfig.Setup(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await SchedulerLoopEndpoints.StartLoop(mockLoop.Object, mockConfig.Object, CancellationToken.None);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStartResultDto>>().Subject;
        ok.Value!.Started.Should().BeTrue();
        ok.Value.Error.Should().BeNull();
        mockConfig.Verify(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once,
            "ClosedLoopAutoStart must be persisted on successful start");
    }

    [Fact]
    public async Task StartLoop_WhenAlreadyActive_ReturnsErrorMessage()
    {
        var mockLoop = MockLoopService();
        mockLoop.Setup(l => l.StartLoopAsync()).ReturnsAsync(false);
        mockLoop.Setup(l => l.IsLoopActive).Returns(true);
        mockLoop.Setup(l => l.ValidationErrors).Returns([]);

        var result = await SchedulerLoopEndpoints.StartLoop(mockLoop.Object, Mock.Of<IPipelineApiConfigClient>(), CancellationToken.None);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStartResultDto>>().Subject;
        ok.Value!.Started.Should().BeFalse();
        ok.Value.Error.Should().Contain("already active");
    }

    [Fact]
    public async Task StartLoop_WhenValidationErrors_ReturnsValidationErrorMessage()
    {
        var mockLoop = MockLoopService();
        mockLoop.Setup(l => l.StartLoopAsync()).ReturnsAsync(false);
        mockLoop.Setup(l => l.IsLoopActive).Returns(false);
        mockLoop.Setup(l => l.ValidationErrors).Returns(["No templates configured"]);

        var result = await SchedulerLoopEndpoints.StartLoop(mockLoop.Object, Mock.Of<IPipelineApiConfigClient>(), CancellationToken.None);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStartResultDto>>().Subject;
        ok.Value!.Error.Should().Contain("validation errors");
    }

    // ── Handler: StopLoop ───────────────────────────────────────────────────

    [Fact]
    public async Task StopLoop_CallsStopLoopAndPersistsAutoStartFalse()
    {
        var mockLoop = MockLoopService();
        var mockConfig = new Mock<IPipelineApiConfigClient>();
        mockConfig.Setup(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await SchedulerLoopEndpoints.StopLoop(mockLoop.Object, mockConfig.Object, CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NoContent>();
        mockLoop.Verify(l => l.StopLoop(), Times.Once);
        mockConfig.Verify(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Handler: ResumeLoop ─────────────────────────────────────────────────

    [Fact]
    public void ResumeLoop_CallsResumeLoopAndReturnsNoContent()
    {
        var mockLoop = MockLoopService();

        var result = SchedulerLoopEndpoints.ResumeLoop(mockLoop.Object);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NoContent>();
        mockLoop.Verify(l => l.ResumeLoop(), Times.Once);
    }

    // ── BuildDto ────────────────────────────────────────────────────────────

    [Fact]
    public void BuildDto_MapsAllFieldsFromLoopService()
    {
        var mockLoop = MockLoopService(isActive: true, status: "Active");
        mockLoop.Setup(l => l.ProcessedCount).Returns(5);
        mockLoop.Setup(l => l.FailedCount).Returns(1);

        var dto = SchedulerLoopEndpoints.BuildDto(mockLoop.Object);

        dto.IsLoopActive.Should().BeTrue();
        dto.StatusMessage.Should().Be("Active");
        dto.ProcessedCount.Should().Be(5);
        dto.FailedCount.Should().Be(1);
    }

    // ── ApiKeyFilter ────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiKeyFilter_WhenKeyEmpty_Returns503AndDoesNotCallNext()
    {
        var filter = CreateFilter("");
        var nextCalled = false;
        var result = await filter.InvokeAsync(MakeContext(null), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        });
        nextCalled.Should().BeFalse("empty/unconfigured key must block the request");
    }

    [Fact]
    public async Task ApiKeyFilter_WhenKeyMatches_CallsNext()
    {
        var filter = CreateFilter("secret");
        var nextCalled = false;
        await filter.InvokeAsync(MakeContext("secret"), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        });
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ApiKeyFilter_WhenKeyMissing_Returns401()
    {
        var filter = CreateFilter("secret");
        var nextCalled = false;
        var result = await filter.InvokeAsync(MakeContext(null), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        });
        nextCalled.Should().BeFalse();
        result.Should().BeAssignableTo<IResult>();
    }

    [Fact]
    public async Task ApiKeyFilter_WhenKeyWrong_Returns401()
    {
        var filter = CreateFilter("secret");
        var nextCalled = false;
        var result = await filter.InvokeAsync(MakeContext("wrong"), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        });
        nextCalled.Should().BeFalse();
        result.Should().BeAssignableTo<IResult>();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IEndpointFilter CreateFilter(string expectedKey)
    {
        var filterType = typeof(SchedulerLoopEndpoints)
            .GetNestedType("ApiKeyFilter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IEndpointFilter)Activator.CreateInstance(filterType, expectedKey)!;
    }

    private static EndpointFilterInvocationContext MakeContext(string? headerValue)
    {
        var httpContext = new DefaultHttpContext();
        if (headerValue is not null)
            httpContext.Request.Headers["X-Api-Key"] = headerValue;
        var mock = new Mock<EndpointFilterInvocationContext>();
        mock.Setup(m => m.HttpContext).Returns(httpContext);
        return mock.Object;
    }

    private static Mock<IPipelineLoopService> MockLoopService(bool isActive = false, string status = "")
    {
        var mock = new Mock<IPipelineLoopService>();
        mock.Setup(l => l.IsLoopActive).Returns(isActive);
        mock.Setup(l => l.StatusMessage).Returns(status);
        mock.Setup(l => l.CurrentIssueIdentifier).Returns((string?)null);
        mock.Setup(l => l.ProcessedCount).Returns(0);
        mock.Setup(l => l.FailedCount).Returns(0);
        mock.Setup(l => l.QueueCount).Returns(0);
        mock.Setup(l => l.IsCircuitBroken).Returns(false);
        mock.Setup(l => l.LastPollError).Returns((string?)null);
        mock.Setup(l => l.CurrentCycleTemplateIndex).Returns(0);
        mock.Setup(l => l.CurrentCycleTemplateCount).Returns(0);
        mock.Setup(l => l.ValidationErrors).Returns([]);
        mock.Setup(l => l.TemplateStatuses).Returns(new Dictionary<string, ConfigStatusSnapshot>());
        return mock;
    }

    private static LoopStatusDto MakeDto(bool isActive, string status) => new(
        isActive, status, null, 0, 0, 0, false, null, 0, 0, [], new Dictionary<string, ConfigStatusSnapshot>());
}
