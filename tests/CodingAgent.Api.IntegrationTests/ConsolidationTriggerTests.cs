using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Spec 051 2.4a: the API registers the full <see cref="IConsolidationService"/>. A trigger resolves the
/// template, project and agent selector from the stores and enqueues a Pending work item through the
/// in-process distributor, where the Scheduler's dispatch loop picks it up.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class ConsolidationTriggerTests(ApiWebApplicationFactory factory)
{
    private const string Project = "6f1c2a9e-0000-0000-0000-0000000000c1";
    private const string Template = "7a000000-0000-0000-0000-0000000000c1";
    private const string RepoProvider = "8b000000-0000-0000-0000-0000000000c1";
    private static readonly string[] RequiredLabels = ["consolidation-trigger-test"];
    private static readonly string[] ProfileLabels = ["consolidation-trigger-test", "kiro"];

    private IConsolidationService Consolidation => factory.Services.GetRequiredService<IConsolidationService>();

    private async Task SeedAsync()
    {
        var projects = factory.Services.GetRequiredService<IProjectStore>();
        await projects.SaveProjectAsync(new PipelineProject { Id = Project, Name = "Charlie" }, CancellationToken.None);
        await projects.SaveTemplateAsync(Project, new PipelineJobTemplate
        {
            Id = Template,
            Name = "charlie-api",
            IssueProviderId = "ip-consolidation",
            RepoProviderId = RepoProvider,
            Enabled = true,
            RequiredLabels = [.. RequiredLabels],
        }, CancellationToken.None);
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IProviderConfigStore>().SaveProviderConfigAsync(new ProviderConfig
        {
            Id = RepoProvider,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Charlie repo",
        }, CancellationToken.None);
        await factory.Services.GetRequiredService<IAgentProfileStore>().SaveAgentProfileAsync(new AgentProfile
        {
            Id = "9c000000-0000-0000-0000-0000000000c1",
            DisplayName = "Charlie agent",
            AgentProviderConfigId = "agent-cfg",
            MatchLabels = ProfileLabels,
            Enabled = true,
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Trigger_EnqueuesAPendingConsolidationItem_ForTheTemplatesProfile()
    {
        await SeedAsync();

        var result = await Consolidation.TriggerAsync(ConsolidationRunType.RefactoringDetection, new TemplateId(Template), CancellationToken.None);

        result.Should().NotBeNull();
        result!.ProjectId.Should().Be(Project);
        await using var db = factory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == Guid.Parse(result.WorkItemId!));
        item.Status.Should().Be(WorkItemStatus.Pending);
        item.TaskType.Should().Be(WorkItemTaskType.Consolidation);
        item.IssueIdentifier.Should().Be($"{ConsolidationRunType.RefactoringDetection}:{Template}");
        item.IssueProviderConfigId.Should().Be(ConsolidationConstants.ProviderConfigId);
        item.AgentSelector.Should().Be(AgentSelectorKey.From(ProfileLabels));
        item.ProjectId.Should().Be(Guid.Parse(Project));

        // The same scope is live now, so a second trigger is refused as a duplicate.
        (await Consolidation.TriggerAsync(ConsolidationRunType.RefactoringDetection, new TemplateId(Template), CancellationToken.None))
            .Should().BeNull();
    }
}
