using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using CodingAgent.Contracts.Auth;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiDispatchCollection : ICollectionFixture<DispatchApiFactory>
{
    public const string Name = "UiDispatch";
}

/// <summary>The API with mocked providers and orchestration, so a dispatch's result is the test's to choose.</summary>
public sealed class DispatchApiFactory : ApiWebApplicationFactory
{
    public Mock<IProviderFactory> Providers { get; } = new();
    public Mock<IDispatchOrchestrationService> Orchestration { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<IProviderFactory>();
        services.AddSingleton(Providers.Object);
        services.RemoveAll<IDispatchOrchestrationService>();
        services.AddSingleton(Orchestration.Object);
    }
}

/// <summary>
/// Spec 051 2.3c: manual dispatch on the UI surface, by template and issue or pull request number. It
/// needs operator on the template's project, and the web's real <see cref="UiDispatchClient"/> drives it,
/// so this doubles as its contract test. TemplateDispatchServiceTests cover the dispatch rules.
/// </summary>
[Collection(UiDispatchCollection.Name)]
public sealed class UiDispatchEndpointTests
{
    private const string ProjectT = "6f1c2a9e-0000-0000-0000-0000000000b7";
    private const string ProjectU = "6f1c2a9e-0000-0000-0000-0000000000b8";
    private const string TemplateT = "7a000000-0000-0000-0000-0000000000b7";
    private const string TemplateOfU = "7a000000-0000-0000-0000-0000000000b8";
    private const string TemplateWithoutProject = "7a000000-0000-0000-0000-0000000000b9";
    private const string IssueProviderT = "8b000000-0000-0000-0000-0000000000b7";
    private const string RepoProviderT = "8b000000-0000-0000-0000-0000000000b8";

    private readonly DispatchApiFactory _factory;
    private readonly Mock<IIssueProvider> _issues = new();
    private readonly Mock<IRepositoryProvider> _repository = new();
    private readonly JobDistributionRequest _prepared = new()
    {
        IssueIdentifier = "7", IssueProviderConfigId = IssueProviderT, RepoProviderConfigId = RepoProviderT, InitiatedBy = "manual",
        TaskType = WorkItemTaskType.Implementation, AgentSelector = "dotnet", TimeoutSeconds = 3600, ProviderConfigs = []
    };

    private sealed class FixedToken(string token) : IUiAccessTokenSource
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => ValueTask.FromResult<string?>(token);
    }

    public UiDispatchEndpointTests(DispatchApiFactory factory)
    {
        _factory = factory;
        factory.Providers.Reset();
        factory.Orchestration.Reset();
        factory.Providers.Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == IssueProviderT))).Returns(_issues.Object);
        factory.Providers.Setup(f => f.CreateRepositoryProvider(It.Is<ProviderConfig>(c => c.Id == RepoProviderT))).Returns(_repository.Object);
        factory.Orchestration.Setup(o => o.PrepareDistributionRequestAsync(It.IsAny<ImplementationDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(_prepared);
        factory.Orchestration.Setup(o => o.PrepareReviewDistributionRequestAsync(It.IsAny<ReviewDispatchRequest>(), It.IsAny<PipelineProject>(), It.IsAny<CancellationToken>())).ReturnsAsync(_prepared);
        factory.Orchestration.Setup(o => o.PrepareDecompositionDistributionRequestAsync(It.IsAny<DecompositionDispatchOrchestrationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(_prepared);
        factory.Orchestration.Setup(o => o.DistributeAndFinalizeAsync(_prepared, It.IsAny<CancellationToken>())).ReturnsAsync(new DispatchOutcome(true, true, null));
    }

    private UiDispatchClient Dispatch(AccessGrant grant) => new(_factory.CreateClient(), new FixedToken(TestUserTokens.For(grant)));

    private HttpClient Http(AccessGrant grant)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant));
        return client;
    }

    private async Task SeedTemplatesAsync()
    {
        var projects = _factory.Services.GetRequiredService<IProjectStore>();
        await projects.SaveProjectAsync(new PipelineProject { Id = ProjectT, Name = "Tango" }, CancellationToken.None);
        await projects.SaveProjectAsync(new PipelineProject { Id = ProjectU, Name = "Uniform" }, CancellationToken.None);
        await projects.SaveTemplateAsync(ProjectT, new PipelineJobTemplate
        {
            Id = TemplateT, Name = "tango-api", IssueProviderId = IssueProviderT, RepoProviderId = RepoProviderT, Enabled = true,
        }, CancellationToken.None);
        await projects.SaveTemplateAsync(ProjectU, new PipelineJobTemplate
        {
            Id = TemplateOfU, Name = "uniform-api", IssueProviderId = IssueProviderT, RepoProviderId = RepoProviderT,
        }, CancellationToken.None);
        using var scope = _factory.Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IProviderConfigStore>();
        await configs.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = IssueProviderT, Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Tango issues",
        }, CancellationToken.None);
        await configs.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = RepoProviderT, Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Tango repo",
        }, CancellationToken.None);
    }

    private void HasIssue(string id, params string[] labels) =>
        _issues.Setup(t => t.GetIssueAsync(It.Is<IssueIdentifier>(i => i.Value == id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = id, Title = $"Issue {id}", Description = "", Labels = labels });

    [Fact]
    public async Task Dispatch_NeedsOperatorOnTheTemplatesProject()
    {
        await SeedTemplatesAsync();
        var operatorOfT = Http(TestUserTokens.Scoped(ProjectT, AccessRole.Operator));
        var readerOfT = Http(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly));

        foreach (var path in new[] { "issues/7/dispatch", "pull-requests/7/dispatch", "epics/7/dispatch" })
        {
            (await operatorOfT.PostAsync($"/api/ui/templates/{TemplateOfU}/{path}", null)).StatusCode.Should().Be(HttpStatusCode.NotFound, path);
            (await operatorOfT.PostAsync($"/api/ui/templates/{TemplateWithoutProject}/{path}", null)).StatusCode.Should().Be(HttpStatusCode.NotFound, path);
            (await operatorOfT.PostAsync($"/api/ui/templates/does-not-exist/{path}", null)).StatusCode.Should().Be(HttpStatusCode.NotFound, path);
            (await readerOfT.PostAsync($"/api/ui/templates/{TemplateT}/{path}", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
        }
        _factory.Orchestration.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchIssue_QueuesTheTrackersIssue_ThroughTheClient()
    {
        await SeedTemplatesAsync();
        HasIssue("7");

        var message = await Dispatch(TestUserTokens.Scoped(ProjectT, AccessRole.Operator)).DispatchIssueAsync(TemplateT, "7");

        message.Should().Be("⏳ Queued #7 — the job controller will start an agent pod for it");
        _factory.Orchestration.Verify(o => o.PrepareDistributionRequestAsync(It.Is<ImplementationDispatchOrchestrationRequest>(r =>
            r.IssueIdentifier.Value == "7" && r.IssueProviderId.Value == IssueProviderT && r.RepoProviderId.Value == RepoProviderT
            && r.Project.Id == ProjectT), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchPullRequest_QueuesTheOpenPullRequest_ThroughTheClient()
    {
        await SeedTemplatesAsync();
        _repository.Setup(r => r.GetOpenPullRequestAsync(12, It.IsAny<CancellationToken>())).ReturnsAsync(new PullRequestSummary
        {
            Number = 12, Identifier = "12", Title = "PR 12", Description = "", Labels = [], BranchName = "feat/x", TargetBranch = "main", Url = "http://x", IsDraft = false
        });

        var message = await Dispatch(TestUserTokens.Global(AccessRole.Operator)).DispatchPullRequestAsync(TemplateT, 12);

        message.Should().Be("⏳ Queued PR #12 for review — the job controller will start an agent pod for it");
        _factory.Orchestration.Verify(o => o.PrepareReviewDistributionRequestAsync(
            It.Is<ReviewDispatchRequest>(r => r.PrNumber == 12 && r.PrBranchName == "feat/x"), It.Is<PipelineProject>(p => p.Id == ProjectT), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchEpic_QueuesTheApprovedEpicForDecomposition_ThroughTheClient()
    {
        await SeedTemplatesAsync();
        HasIssue("30", AgentLabels.EpicApproved);

        var message = await Dispatch(TestUserTokens.Scoped(ProjectT, AccessRole.Operator)).DispatchEpicAsync(TemplateT, "30");

        message.Should().Be("⏳ Queued epic #30 for decomposition — the job controller will start an agent pod for it");
        _factory.Orchestration.Verify(o => o.PrepareDecompositionDistributionRequestAsync(
            It.Is<DecompositionDispatchOrchestrationRequest>(r => r.PhaseType == PipelineRunType.Decomposition && r.EpicIdentifier.Value == "30"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispatch_ARefusal_ReachesThePageAsItsReason()
    {
        await SeedTemplatesAsync();
        HasIssue("9", AgentLabels.EpicReview);
        _repository.Setup(r => r.GetOpenPullRequestAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync((PullRequestSummary?)null);
        var client = Dispatch(TestUserTokens.Scoped(ProjectT, AccessRole.Operator));

        var issue = () => client.DispatchIssueAsync(TemplateT, "9");
        var pr = () => client.DispatchPullRequestAsync(TemplateT, 5);

        (await issue.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("requires human approval");
        (await pr.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("PR #5 is not open.");
        _factory.Orchestration.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Dispatch_AFailingProvider_ReachesThePageAsItsReason()
    {
        await SeedTemplatesAsync();
        _issues.Setup(t => t.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("rate limited"));

        var epic = () => Dispatch(TestUserTokens.Global(AccessRole.Operator)).DispatchEpicAsync(TemplateT, "30");

        (await epic.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("Failed to load issue #30: rate limited");
    }
}
