using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// Spec 051 2.4b: the Consolidation page on the UI surface. Reading needs global read and triggering
/// global operator, as the page required before; a project role is not enough, because a brain and its
/// consolidation can serve several projects. The web's real <see cref="UiConsolidationClient"/> drives
/// the round trips, so this doubles as its contract test. ConsolidationPageServiceTests cover what the
/// cards say.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiConsolidationEndpointTests(ApiWebApplicationFactory factory)
{
    private const string Project = "6f1c2a9e-0000-0000-0000-0000000000d1";
    private const string Template = "7a000000-0000-0000-0000-0000000000d1";
    private const string RepoProvider = "8b000000-0000-0000-0000-0000000000d1";
    private const string Profile = "9c000000-0000-0000-0000-0000000000d1";
    private static readonly string[] Labels = ["consolidation-ui-test"];

    private sealed class FixedToken(string token) : IUiAccessTokenSource
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => ValueTask.FromResult<string?>(token);
    }

    private UiConsolidationClient Client(AccessGrant grant) => new(factory.CreateClient(), new FixedToken(TestUserTokens.For(grant)));

    private HttpClient Http(AccessGrant grant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant));
        return client;
    }

    private async Task SeedAsync()
    {
        var projects = factory.Services.GetRequiredService<IProjectStore>();
        await projects.SaveProjectAsync(new PipelineProject { Id = Project, Name = "Delta", MaxRefactoringProposals = 9 }, CancellationToken.None);
        await projects.SaveTemplateAsync(Project, new PipelineJobTemplate
        {
            Id = Template,
            Name = "delta-api",
            IssueProviderId = "ip-delta",
            RepoProviderId = RepoProvider,
            BrainProviderId = "brain-delta",
            Enabled = true,
            RequiredLabels = [.. Labels],
        }, CancellationToken.None);
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IProviderConfigStore>().SaveProviderConfigAsync(new ProviderConfig
        {
            Id = RepoProvider,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Delta repo",
        }, CancellationToken.None);
        await factory.Services.GetRequiredService<IAgentProfileStore>().SaveAgentProfileAsync(new AgentProfile
        {
            Id = Profile,
            DisplayName = "Delta agent",
            AgentProviderConfigId = "agent-cfg",
            MatchLabels = Labels,
            Enabled = true,
        }, CancellationToken.None);
    }

    [IdorCase("GET /api/ui/consolidation/templates/{templateId}/refactoring-scan")]
    [Fact]
    public async Task Reading_NeedsGlobalRead_AProjectRoleIsNotEnough()
    {
        var projectOperator = Http(TestUserTokens.Scoped(Project, AccessRole.Operator));

        (await projectOperator.GetAsync("/api/ui/consolidation")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await projectOperator.GetAsync($"/api/ui/consolidation/templates/{Template}/refactoring-scan")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Http(TestUserTokens.Global(AccessRole.ReadOnly)).GetAsync("/api/ui/consolidation")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Triggering_NeedsGlobalOperate()
    {
        var body = new UiConsolidationTriggerRequest(ConsolidationRunType.HarnessSuggestions, null, false);

        (await Http(TestUserTokens.Global(AccessRole.ReadOnly)).PostAsJsonAsync("/api/ui/consolidation/runs", body))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Http(TestUserTokens.Scoped(Project, AccessRole.Operator)).PostAsJsonAsync("/api/ui/consolidation/runs", body))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Overview_AndRefactoringScan_RoundTripThroughTheClient()
    {
        await SeedAsync();
        var reader = Client(TestUserTokens.Global(AccessRole.ReadOnly));

        var overview = await reader.GetOverviewAsync();
        var scan = await reader.GetRefactoringScanSettingsAsync(Template);

        overview.Templates.Should().ContainEquivalentOf(new UiConsolidationTemplate(Template, "delta-api", true, true, false));
        overview.BrainProviderByTemplate.Should().Contain(Template, "brain-delta");
        scan.MaxProposals.Should().Be(9);
        scan.MaxProposalsIsProjectOverride.Should().BeTrue();
    }

    [Fact]
    public async Task Trigger_QueuesTheRun_AndASecondIsRefusedWithTheReason()
    {
        await SeedAsync();
        var operatorClient = Client(TestUserTokens.Global(AccessRole.Operator));

        var queued = await operatorClient.TriggerAsync(ConsolidationRunType.RefactoringDetection, Template, autoDispatch: false);
        var again = () => operatorClient.TriggerAsync(ConsolidationRunType.RefactoringDetection, Template, autoDispatch: false);

        queued.Should().Be("Refactoring Detection queued — waiting for an idle agent.");
        (await again.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("Refactoring scan rejected for delta-api — already running*");
    }

    [Fact]
    public async Task AutoCounters_NeedsGlobalRead_AndReturnsEmptyWhenThresholdsAreZero()
    {
        // Authorization: a project-scoped role is not enough (same as the overview endpoint).
        (await Http(TestUserTokens.Scoped(Project, AccessRole.Operator))
            .GetAsync("/api/ui/consolidation/auto-counters"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // With global read and all auto-start thresholds at their default (0), all counter
        // dictionaries are empty and the harness counter is null.
        var counters = await Client(TestUserTokens.Global(AccessRole.ReadOnly)).GetAutoCountersAsync();

        counters.BrainCounters.Should().BeEmpty();
        counters.RefactoringCounters.Should().BeEmpty();
        counters.HarnessCounter.Should().BeNull();
    }
}
