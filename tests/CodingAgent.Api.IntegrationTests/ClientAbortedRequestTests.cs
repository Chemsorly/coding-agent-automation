using System.Net.Http.Headers;
using AwesomeAssertions;
using CodingAgent.Api.IntegrationTests.Security;
using CodingAgent.Contracts.Auth;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Serilog.Events;
using Xunit;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// A request the client aborts mid-call (the browser navigated away) ends as 499 Client Closed Request at
/// Debug, not as an unhandled <see cref="OperationCanceledException"/> the request log reports as a 500
/// Warning (<see cref="ClientAbortedRequests"/>).
/// </summary>
public sealed class ClientAbortedRequestTests
{
    private const string BacklogPath = "/api/ui/backlog";
    private const string ProjectId = "6f1c2a9e-0000-0000-0000-0000000000d1";
    private const string TemplateId = "7a000000-0000-0000-0000-0000000000d1";
    private const string IssueProviderId = "8b000000-0000-0000-0000-0000000000d1";

    /// <summary>The walkthrough's case: the backlog call is aborted while the issue provider is still reading.</summary>
    [Fact]
    public async Task AbortedBacklogRequest_EndsAs499_WithoutAnException()
    {
        await using var factory = new OutcomeRecordingFactory();
        var issuesRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await SeedTemplateAsync(factory);
        var issues = new Mock<IIssueProvider>();
        issues.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (int _, int _, CancellationToken ct) =>
            {
                issuesRequested.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new PagedResult<IssueSummary> { Items = [], Page = 1, PageSize = 50, HasMore = false };
            });
        Mock.Get(factory.Services.GetRequiredService<IProviderFactory>())
            .Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == IssueProviderId)))
            .Returns(issues.Object);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestUserTokens.For(TestUserTokens.Global(AccessRole.ReadOnly)));
        using var abort = new CancellationTokenSource();
        var request = client.GetAsync(BacklogPath, abort.Token);
        await issuesRequested.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await abort.CancelAsync();
        await FluentActions.Awaiting(() => request).Should().ThrowAsync<OperationCanceledException>();

        var (status, error) = await factory.Outcome.Task.WaitAsync(TimeSpan.FromSeconds(30));
        error.Should().BeNull("an aborted request is not a server error");
        status.Should().Be(StatusCodes.Status499ClientClosedRequest);
    }

    [Fact]
    public async Task OperationCanceled_WithoutAnAbortedRequest_IsRethrown()
    {
        // A timeout inside the API, for example, is a real failure and stays a 500.
        var context = new DefaultHttpContext();

        var act = () => ClientAbortedRequests.EndAbortedRequestAsync(context, _ => throw new OperationCanceledException());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task OperationCanceled_AfterTheResponseStarted_IsSwallowed_AndTheStatusKept()
    {
        var context = AbortedContext(responseStarted: true);
        context.Response.StatusCode = StatusCodes.Status200OK;

        await ClientAbortedRequests.EndAbortedRequestAsync(context, _ => throw new OperationCanceledException());

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData(StatusCodes.Status200OK, false, LogEventLevel.Debug)]
    [InlineData(StatusCodes.Status499ClientClosedRequest, false, LogEventLevel.Debug)]
    [InlineData(StatusCodes.Status404NotFound, false, LogEventLevel.Warning)]
    [InlineData(StatusCodes.Status500InternalServerError, false, LogEventLevel.Warning)]
    [InlineData(StatusCodes.Status200OK, true, LogEventLevel.Warning)]
    public void RequestLogLevel_KeepsAbortedRequestsAtDebug(int status, bool exception, LogEventLevel expected)
    {
        var context = new DefaultHttpContext();
        context.Response.StatusCode = status;

        ClientAbortedRequests.GetRequestLogLevel(context, exception ? new InvalidOperationException() : null)
            .Should().Be(expected);
    }

    private static DefaultHttpContext AbortedContext(bool responseStarted)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new ResponseFeature(responseStarted));
        context.RequestAborted = new CancellationToken(canceled: true);
        return context;
    }

    private static async Task SeedTemplateAsync(OutcomeRecordingFactory factory)
    {
        var projects = factory.Services.GetRequiredService<IProjectStore>();
        await projects.SaveProjectAsync(new PipelineProject { Id = ProjectId, Name = "Delta" }, CancellationToken.None);
        await projects.SaveTemplateAsync(ProjectId, new PipelineJobTemplate
        {
            Id = TemplateId, Name = "delta-api", IssueProviderId = IssueProviderId, RepoProviderId = "rp-aborted", Enabled = true,
        }, CancellationToken.None);
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IProviderConfigStore>().SaveProviderConfigAsync(new ProviderConfig
        {
            Id = IssueProviderId, Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Delta issues",
        }, CancellationToken.None);
    }

    private sealed class ResponseFeature(bool hasStarted) : HttpResponseFeature
    {
        public override bool HasStarted => hasStarted;
    }

    /// <summary>
    /// The API with an outermost middleware that records how the backlog request ended: its status, and the
    /// exception that left the API's own pipeline, if any.
    /// </summary>
    private sealed class OutcomeRecordingFactory : ApiWebApplicationFactory
    {
        public TaskCompletionSource<(int Status, Exception? Error)> Outcome { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override void ConfigureTestServices(IServiceCollection services) =>
            services.AddSingleton<IStartupFilter>(new RecordingStartupFilter(Outcome));
    }

    private sealed class RecordingStartupFilter(TaskCompletionSource<(int Status, Exception? Error)> outcome) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path != BacklogPath)
                {
                    await nextMiddleware(context);
                    return;
                }

                try
                {
                    await nextMiddleware(context);
                    outcome.TrySetResult((context.Response.StatusCode, null));
                }
                catch (Exception ex)
                {
                    outcome.TrySetResult((context.Response.StatusCode, ex));
                    throw;
                }
            });
            next(app);
        };
    }
}
