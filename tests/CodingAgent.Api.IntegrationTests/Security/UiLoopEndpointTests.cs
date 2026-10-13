using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Api.Ui;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// Spec 051 2.6 (D11): loop control on the UI surface. Start, Stop and Resume need global operator and only
/// write the pipeline config; the status is what the Scheduler leader last pushed to
/// <c>PUT /api/scheduler/loop-status</c>, filtered for project-scoped users (D3 rule 5).
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiLoopEndpointTests(ApiWebApplicationFactory factory)
{
    private const string ProjectA = "6f1c2a9e-0000-0000-0000-0000000000e1";
    private const string ProjectB = "6f1c2a9e-0000-0000-0000-0000000000e2";
    private const string TemplateA = "7a000000-0000-0000-0000-0000000000e1";
    private const string TemplateB = "7a000000-0000-0000-0000-0000000000e2";

    private HttpClient Http(AccessGrant grant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant));
        return client;
    }

    private HttpClient Scheduler()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
        return client;
    }

    private IPipelineConfigStore Config => factory.Services.GetRequiredService<IPipelineConfigStore>();

    private static LoopStatusDto Reported() => new(
        IsLoopActive: true,
        StatusMessage: "🔄 Dispatching #12 from 'beta-api'",
        CurrentIssueIdentifier: "12",
        ProcessedCount: 4,
        FailedCount: 1,
        QueueCount: 2,
        IsCircuitBroken: false,
        LastPollError: "beta-repo: rate limited",
        CurrentCycleTemplateIndex: 1,
        CurrentCycleTemplateCount: 2,
        ValidationErrors: ["Template 'beta-api' references non-existent repo provider 'x'."],
        TemplateStatuses: new Dictionary<string, ConfigStatusSnapshot>
        {
            [TemplateA] = new() { LastPollIssueCount = 3 },
            [TemplateB] = new() { LastPollIssueCount = 5 },
        });

    private async Task SeedProjectsAsync()
    {
        var projects = factory.Services.GetRequiredService<IProjectStore>();
        await projects.SaveProjectAsync(new PipelineProject { Id = ProjectA, Name = "Alpha" }, CancellationToken.None);
        await projects.SaveProjectAsync(new PipelineProject { Id = ProjectB, Name = "Beta" }, CancellationToken.None);
        await projects.SaveTemplateAsync(ProjectA, new PipelineJobTemplate { Id = TemplateA, Name = "alpha-api", IssueProviderId = "ip", RepoProviderId = "rp" }, CancellationToken.None);
        await projects.SaveTemplateAsync(ProjectB, new PipelineJobTemplate { Id = TemplateB, Name = "beta-api", IssueProviderId = "ip", RepoProviderId = "rp" }, CancellationToken.None);
    }

    private async Task<UiLoopStatus> GetStatusAsync(AccessGrant grant)
    {
        var response = await Http(grant).GetAsync("/api/ui/loop/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<UiLoopStatus>())!;
    }

    // ── Commands ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("resume")]
    public async Task Commands_NeedGlobalOperate(string command)
    {
        (await Http(TestUserTokens.Global(AccessRole.ReadOnly)).PostAsync($"/api/ui/loop/{command}", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Http(TestUserTokens.Scoped(ProjectA, AccessRole.Operator)).PostAsync($"/api/ui/loop/{command}", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Scheduler().PostAsync($"/api/ui/loop/{command}", null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the UI surface accepts only user tokens");
    }

    [Fact]
    public async Task StartAndStop_WriteTheDesiredState()
    {
        var operatorClient = Http(TestUserTokens.Global(AccessRole.Operator));

        (await operatorClient.PostAsync("/api/ui/loop/start", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Config.LoadPipelineConfigAsync(CancellationToken.None)).ClosedLoopAutoStart.Should().BeTrue();

        (await operatorClient.PostAsync("/api/ui/loop/stop", null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Config.LoadPipelineConfigAsync(CancellationToken.None)).ClosedLoopAutoStart.Should().BeFalse();
    }

    [Fact]
    public async Task Resume_StampsTheRequestTime()
    {
        var before = DateTimeOffset.UtcNow;

        (await Http(TestUserTokens.Global(AccessRole.Operator)).PostAsync("/api/ui/loop/resume", null))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

        (await Config.LoadPipelineConfigAsync(CancellationToken.None)).LoopResumeRequestedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task SavingSettings_KeepsTheStoredLoopCommands()
    {
        var requestedAt = DateTimeOffset.UtcNow;
        await Config.UpdatePipelineConfigAsync(c => c with { ClosedLoopAutoStart = true, LoopResumeRequestedAt = requestedAt }, CancellationToken.None);
        var staleForm = (await Config.LoadPipelineConfigAsync(CancellationToken.None)) with { ClosedLoopAutoStart = false, LoopResumeRequestedAt = null, MaxRetries = 4 };

        (await Http(TestUserTokens.Global(AccessRole.Admin)).PutAsJsonAsync("/api/ui/settings/pipeline", staleForm, PipelineJsonOptions.Default))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = await Config.LoadPipelineConfigAsync(CancellationToken.None);
        saved.MaxRetries.Should().Be(4);
        saved.ClosedLoopAutoStart.Should().BeTrue();
        saved.LoopResumeRequestedAt.Should().Be(requestedAt);
    }

    // ── Status ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task StatusPush_NeedsTheOperatorKey()
    {
        (await Http(TestUserTokens.Global(AccessRole.Admin)).PutAsJsonAsync("/api/scheduler/loop-status", Reported(), PipelineJsonOptions.Default))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Scheduler().PutAsJsonAsync("/api/scheduler/loop-status", Reported(), PipelineJsonOptions.Default))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Status_GlobalReader_SeesTheWholeReportAndTheDesiredState()
    {
        await Config.UpdatePipelineConfigAsync(c => c with { ClosedLoopAutoStart = true }, CancellationToken.None);
        (await Scheduler().PutAsJsonAsync("/api/scheduler/loop-status", Reported(), PipelineJsonOptions.Default)).EnsureSuccessStatusCode();

        var status = await GetStatusAsync(TestUserTokens.Global(AccessRole.ReadOnly));

        status.IsLoopActive.Should().BeTrue();
        status.RunRequested.Should().BeTrue();
        status.StatusMessage.Should().Be("🔄 Dispatching #12 from 'beta-api'");
        status.CurrentIssueIdentifier.Should().Be("12");
        status.LastPollError.Should().Be("beta-repo: rate limited");
        status.ValidationErrors.Should().ContainSingle();
        status.TemplateStatuses.Keys.Should().BeEquivalentTo([TemplateA, TemplateB]);
        status.ProcessedCount.Should().Be(4);
        status.ReportedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Status_ProjectScopedUser_SeesOnlyTheirTemplatesAndNoMessages()
    {
        await SeedProjectsAsync();
        (await Scheduler().PutAsJsonAsync("/api/scheduler/loop-status", Reported(), PipelineJsonOptions.Default)).EnsureSuccessStatusCode();

        var status = await GetStatusAsync(TestUserTokens.Scoped(ProjectA, AccessRole.ReadOnly));

        status.IsLoopActive.Should().BeTrue();
        status.TemplateStatuses.Keys.Should().BeEquivalentTo([TemplateA]);
        status.StatusMessage.Should().BeEmpty();
        status.CurrentIssueIdentifier.Should().BeNull();
        status.LastPollError.Should().BeNull();
        status.ValidationErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task Status_NoRecentReport_SaysTheSchedulerIsNotReporting()
    {
        var access = new Moq.Mock<IUserAccess>();
        access.Setup(a => a.CanRead(null)).Returns(true);
        var neverReported = new CodingAgent.Api.Loop.LoopStatusStore(null, TimeProvider.System);

        var result = await UiLoopEndpoints.GetStatus(
            access.Object, neverReported, Config, factory.Services.GetRequiredService<IProjectStore>(), CancellationToken.None);

        var status = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<UiLoopStatus>>().Subject.Value!;
        status.IsLoopActive.Should().BeFalse();
        status.StatusMessage.Should().Be(UiLoopEndpoints.NotReportingMessage);
        status.ReportedAt.Should().BeNull();
    }
}
