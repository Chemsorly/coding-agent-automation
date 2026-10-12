using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using CodingAgent.Contracts.Auth;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// Spec 051 2.3b: the backlog on the UI surface. It follows D3 rule 3 like the run lists, and the
/// web's real <see cref="UiBrowseClient"/> reads it, so this doubles as its contract test.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiBrowseEndpointTests(ApiWebApplicationFactory factory)
{
    // Not ProjectB: UiSurfaceTests expects that one to stay unknown.
    private const string ProjectT = "6f1c2a9e-0000-0000-0000-0000000000a7";
    private const string ProjectU = "6f1c2a9e-0000-0000-0000-0000000000a8";
    private const string TemplateT = "7a000000-0000-0000-0000-0000000000a7";
    private const string IssueProviderT = "8b000000-0000-0000-0000-0000000000a7";

    private sealed class FixedToken(string token) : IUiAccessTokenSource
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => ValueTask.FromResult<string?>(token);
    }

    private UiBrowseClient Browse(AccessGrant grant) => new(factory.CreateClient(), new FixedToken(TestUserTokens.For(grant)));

    private HttpClient Http(AccessGrant grant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant));
        return client;
    }

    [Fact]
    public async Task Backlog_WithoutAProject_NeedsGlobalRead_AndAnotherProjectIsNotFound()
    {
        var readerOfT = Http(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly));

        (await readerOfT.GetAsync("/api/ui/backlog")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await readerOfT.GetAsync($"/api/ui/backlog?projectId={ProjectU}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Http(TestUserTokens.Global(AccessRole.ReadOnly)).GetAsync("/api/ui/backlog")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Backlog_ListsTheProjectsIssues_WithTheirReadiness()
    {
        var projects = factory.Services.GetRequiredService<IProjectStore>();
        await projects.SaveProjectAsync(new PipelineProject { Id = ProjectT, Name = "Tango" }, CancellationToken.None);
        await projects.SaveTemplateAsync(ProjectT, new PipelineJobTemplate
        {
            Id = TemplateT, Name = "tango-api", IssueProviderId = IssueProviderT, RepoProviderId = "rp-browse", Enabled = true,
        }, CancellationToken.None);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProviderConfigStore>().SaveProviderConfigAsync(new ProviderConfig
            {
                Id = IssueProviderT, Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Tango issues",
            }, CancellationToken.None);

        var issues = new Mock<IIssueProvider>();
        var page = new PagedResult<IssueSummary>
        {
            Items =
            [
                new IssueSummary { Identifier = "7", Title = "Ready", Labels = ["bug"], Description = "", Url = "https://x/issues/7" },
                new IssueSummary { Identifier = "8", Title = "Parked", Labels = ["backlog"], Description = "", Url = "https://x/issues/8" },
            ],
            Page = 1, PageSize = 50, HasMore = false,
        };
        issues.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(page);
        issues.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(page);
        Mock.Get(factory.Services.GetRequiredService<IProviderFactory>())
            .Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == IssueProviderT)))
            .Returns(issues.Object);

        var backlog = await Browse(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly)).GetBacklogAsync(ProjectT);

        backlog.IsTruncated.Should().BeFalse();
        backlog.Issues.Select(i => (i.Identifier, i.IsReady)).Should().BeEquivalentTo([("7", true), ("8", false)]);
        backlog.Issues.Single(i => i.Identifier == "7").Labels.Should().Equal("bug");
        backlog.BlockedIssues().Should().BeEmpty("a parked issue is not ready, but nothing blocks it");
    }
}
