using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.IntegrationTests.Helpers;

namespace CodingAgentWebUI.IntegrationTests.Pipeline;

/// <summary>
/// Integration tests for the multi-repo pipeline loop (spec 013).
/// Tests full cycle with mixed template health, CRUD persistence, and pre-start validation.
/// </summary>
[Trait("Category", "Integration")]
public class MultiRepoLoopIntegrationTests : IntegrationTestBase
{
    /// <summary>
    /// 13.1: Full cycle with mixed template health.
    /// 3 templates: 1 healthy (returns issues, dispatches succeed), 1 failing (throws on poll),
    /// 1 rate-limited (throws RateLimitExceededException).
    /// Verifies: healthy template dispatches, failing template records error, rate-limited template skips.
    /// </summary>
    [Fact]
    public async Task FullCycle_MixedTemplateHealth_HealthyDispatches_FailingRecordsError_RateLimitedSkips()
    {
        // Arrange: 3 templates with different health states
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "healthy-1", Name = "Healthy", IssueProviderId = "ip-healthy", RepoProviderId = "rp-1", Enabled = true },
            new() { Id = "failing-1", Name = "Failing", IssueProviderId = "ip-failing", RepoProviderId = "rp-1", Enabled = true },
            new() { Id = "ratelimited-1", Name = "RateLimited", IssueProviderId = "ip-ratelimited", RepoProviderId = "rp-1", Enabled = true }
        };

        var config = new PipelineConfiguration
        {
            Workspace = new WorkspaceConfiguration { WorkspaceBaseDirectory = WorkspaceBase },
            PipelineJobTemplates = templates,
            ClosedLoop = new ClosedLoopConfiguration
            {
                PollInterval = TimeSpan.FromMilliseconds(50),
                MaxConsecutivePollFailures = 5
            }
        };
        await ConfigStore.SavePipelineConfigAsync(config, CancellationToken.None);

        // Setup provider configs
        var issueProviderConfigs = new List<ProviderConfig>
        {
            new() { Id = "ip-healthy", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Healthy Provider" },
            new() { Id = "ip-failing", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Failing Provider" },
            new() { Id = "ip-ratelimited", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "RateLimited Provider" }
        };
        var repoProviderConfigs = new List<ProviderConfig>
        {
            new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" }
        };

        // Save provider configs
        foreach (var pc in issueProviderConfigs)
            await ConfigStore.SaveProviderConfigAsync(pc, CancellationToken.None);
        foreach (var pc in repoProviderConfigs)
            await ConfigStore.SaveProviderConfigAsync(pc, CancellationToken.None);

        // Setup mock providers
        var healthyProvider = new Mock<IIssueProvider>();
        healthyProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "101", Title = "Healthy Issue", Labels = new[] { "agent:next" } }
                },
                Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
            });

        var failingProvider = new Mock<IIssueProvider>();
        failingProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var rateLimitedProvider = new Mock<IIssueProvider>();
        rateLimitedProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RateLimitExceededException(DateTimeOffset.UtcNow.AddMinutes(5)));

        MockFactory.Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == "ip-healthy")))
            .Returns(healthyProvider.Object);
        MockFactory.Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == "ip-failing")))
            .Returns(failingProvider.Object);
        MockFactory.Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == "ip-ratelimited")))
            .Returns(rateLimitedProvider.Object);

        // Setup dispatcher
        var mockDispatcher = new Mock<IJobDispatcher>();
        var dispatchedIssues = new List<string>();
        mockDispatcher.Setup(d => d.TryDispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string?, string?, string, CancellationToken>(
                (issueId, _, _, _, _, _, _) => dispatchedIssues.Add(issueId))
            .ReturnsAsync(true);

        var orchestration = new PipelineOrchestrationService(
            ConfigStore, MockFactory.Object, new IssueDescriptionParser(),
            new AgentExecutionOrchestrator(MockLogger.Object),
            new QualityGateOrchestrator(MockValidator.Object, new PullRequestOrchestrator(MockLogger.Object), MockLogger.Object),
            MockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: new Mock<IPipelineRunHistoryService>().Object);

        var loopService = new PipelineLoopService(
            orchestration, MockFactory.Object, ConfigStore, MockLogger.Object, mockDispatcher.Object);

        using var cts = new CancellationTokenSource();
        await loopService.StartAsync(cts.Token);

        // Act: Start the loop
        var started = await loopService.StartLoopAsync();
        started.Should().BeTrue();

        // Wait for at least one cycle to complete
        await Task.Delay(2000);

        // Assert BEFORE stopping (statuses are cleared on stop)
        // Healthy template should have dispatched
        dispatchedIssues.Should().Contain("101");

        // Failing template should have error recorded
        loopService.TemplateStatuses.TryGetValue("failing-1", out var failingStatus);
        failingStatus.Should().NotBeNull();
        failingStatus!.LastError.Should().Contain("Connection refused");
        failingStatus.ConsecutiveFailures.Should().BeGreaterThan(0);

        // Rate-limited template should have rate limit recorded
        loopService.TemplateStatuses.TryGetValue("ratelimited-1", out var rateLimitedStatus);
        rateLimitedStatus.Should().NotBeNull();
        rateLimitedStatus!.RateLimitResetAt.Should().NotBeNull();

        // Cleanup
        loopService.StopLoop();
        await Task.Delay(500);
        await cts.CancelAsync();
    }

    /// <summary>
    /// 13.2: Template CRUD and persistence.
    /// Add template, verify persisted; remove template, verify removed; enable/disable, verify field updated.
    /// </summary>
    [Fact]
    public async Task TemplateCrud_AddRemoveEnableDisable_PersistsCorrectly()
    {
        // Arrange: Start with empty templates
        var config = new PipelineConfiguration
        {
            Workspace = new WorkspaceConfiguration { WorkspaceBaseDirectory = WorkspaceBase },
            PipelineJobTemplates = new List<PipelineJobTemplate>()
        };
        await ConfigStore.SavePipelineConfigAsync(config, CancellationToken.None);

        // Act 1: Add a template
        var newTemplate = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Template",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            Enabled = true
        };

        var loaded = await ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        var updatedTemplates = loaded.PipelineJobTemplates.ToList();
        updatedTemplates.Add(newTemplate);
        var updated = loaded with { PipelineJobTemplates = updatedTemplates };
        await ConfigStore.SavePipelineConfigAsync(updated, CancellationToken.None);

        // Assert 1: Template is persisted
        var freshStore = new JsonConfigurationStore(ConfigDir);
        var reloaded = await freshStore.LoadPipelineConfigAsync(CancellationToken.None);
        reloaded.PipelineJobTemplates.Should().HaveCount(1);
        reloaded.PipelineJobTemplates[0].Name.Should().Be("Test Template");
        reloaded.PipelineJobTemplates[0].IssueProviderId.Should().Be("ip-1");
        reloaded.PipelineJobTemplates[0].RepoProviderId.Should().Be("rp-1");
        reloaded.PipelineJobTemplates[0].Enabled.Should().BeTrue();

        // Act 2: Disable the template
        var templates2 = reloaded.PipelineJobTemplates.ToList();
        templates2[0] = templates2[0] with { Enabled = false };
        await freshStore.SavePipelineConfigAsync(reloaded with { PipelineJobTemplates = templates2 }, CancellationToken.None);

        // Assert 2: Disabled state persisted
        var reloaded2 = await new JsonConfigurationStore(ConfigDir).LoadPipelineConfigAsync(CancellationToken.None);
        reloaded2.PipelineJobTemplates[0].Enabled.Should().BeFalse();

        // Act 3: Remove the template
        await freshStore.SavePipelineConfigAsync(reloaded2 with { PipelineJobTemplates = new List<PipelineJobTemplate>() }, CancellationToken.None);

        // Assert 3: Template removed
        var reloaded3 = await new JsonConfigurationStore(ConfigDir).LoadPipelineConfigAsync(CancellationToken.None);
        reloaded3.PipelineJobTemplates.Should().BeEmpty();
    }

    /// <summary>
    /// 13.3: Pre-start validation rejects invalid templates.
    /// Create templates referencing non-existent provider IDs, call StartLoop(), verify returns false
    /// with correct error messages.
    /// </summary>
    [Fact]
    public async Task PreStartValidation_InvalidProviderIds_RejectWithErrors()
    {
        // Arrange: Templates with non-existent provider IDs
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t-1", Name = "Valid Template", IssueProviderId = "ip-exists", RepoProviderId = "rp-exists", Enabled = true },
            new() { Id = "t-2", Name = "Bad Issue Provider", IssueProviderId = "ip-nonexistent", RepoProviderId = "rp-exists", Enabled = true },
            new() { Id = "t-3", Name = "Bad Repo Provider", IssueProviderId = "ip-exists", RepoProviderId = "rp-nonexistent", Enabled = true }
        };

        var config = new PipelineConfiguration
        {
            Workspace = new WorkspaceConfiguration { WorkspaceBaseDirectory = WorkspaceBase },
            PipelineJobTemplates = templates
        };
        await ConfigStore.SavePipelineConfigAsync(config, CancellationToken.None);

        // Save only the valid provider configs
        await ConfigStore.SaveProviderConfigAsync(
            new ProviderConfig { Id = "ip-exists", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Exists" },
            CancellationToken.None);
        await ConfigStore.SaveProviderConfigAsync(
            new ProviderConfig { Id = "rp-exists", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Exists" },
            CancellationToken.None);

        var orchestration = new PipelineOrchestrationService(
            ConfigStore, MockFactory.Object, new IssueDescriptionParser(),
            new AgentExecutionOrchestrator(MockLogger.Object),
            new QualityGateOrchestrator(MockValidator.Object, new PullRequestOrchestrator(MockLogger.Object), MockLogger.Object),
            MockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: new Mock<IPipelineRunHistoryService>().Object);

        var loopService = new PipelineLoopService(
            orchestration, MockFactory.Object, ConfigStore, MockLogger.Object);

        // Act
        var started = await loopService.StartLoopAsync();

        // Assert
        started.Should().BeFalse();
        loopService.IsLoopActive.Should().BeFalse();
        loopService.ValidationErrors.Should().HaveCountGreaterThan(0);
        loopService.ValidationErrors.Should().Contain(e => e.Contains("Bad Issue Provider") && e.Contains("ip-nonexistent"));
        loopService.ValidationErrors.Should().Contain(e => e.Contains("Bad Repo Provider") && e.Contains("rp-nonexistent"));
    }
}
