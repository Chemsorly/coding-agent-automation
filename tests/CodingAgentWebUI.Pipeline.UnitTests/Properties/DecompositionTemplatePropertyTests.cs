using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using FsCheck;
using FsCheck.Xunit;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for decomposition template enablement and provider validation.
/// Feature: 027-epic-decomposition-pipeline, Properties P1, P2
/// </summary>
public class DecompositionTemplatePropertyTests
{
    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property P1: Template Enablement Independence
    /// For any PipelineJobTemplate with random values of Enabled, ImplementationEnabled,
    /// ReviewEnabled, and DecompositionEnabled: decomposition polling occurs if and only if
    /// Enabled AND DecompositionEnabled is true, independently of ImplementationEnabled and ReviewEnabled.
    /// **Validates: Requirements 1.2, 1.4, 1.6**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task DecompositionPolling_OccursIffEnabledAndDecompositionEnabled(
        bool enabled, bool implementationEnabled, bool reviewEnabled, bool decompositionEnabled)
    {
        var templateId = Guid.NewGuid().ToString();
        var issueProviderId = "ip-test";
        var repoProviderId = "rp-test";

        var template = new PipelineJobTemplate
        {
            Id = templateId,
            Name = "Test-Template",
            IssueProviderId = issueProviderId,
            RepoProviderId = repoProviderId,
            Enabled = enabled,
            ImplementationEnabled = implementationEnabled,
            ReviewEnabled = reviewEnabled,
            DecompositionEnabled = decompositionEnabled
        };

        var config = new PipelineConfiguration
        {
            ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50),
            ClosedLoopMaxRunsPerCycle = 10,
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = new List<string> { template.Id } }
            });
        mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        // Set up both issue and repo provider configs so provider validation passes
        mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = issueProviderId, Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" }
            });
        mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = repoProviderId, Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" }
            });

        var decompositionPolled = false;
        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns<ProviderConfig>(cfg =>
            {
                var mock = new Mock<IIssueProvider>();
                // Implementation/review polling — return empty
                mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                        It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                    .Returns<int, int, IReadOnlyList<string>?, CancellationToken>((page, pageSize, labels, ct) =>
                    {
                        // Detect decomposition polling by checking for epic labels
                        if (labels != null && (labels.Contains(AgentLabels.Epic) || labels.Contains(AgentLabels.EpicApproved)))
                        {
                            decompositionPolled = true;
                        }
                        return Task.FromResult(new PagedResult<IssueSummary>
                        {
                            Items = new List<IssueSummary>(),
                            Page = 1,
                            PageSize = PipelineConstants.DefaultPageSize,
                            HasMore = false
                        });
                    });
                return mock.Object;
            });
        mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(new Mock<IRepositoryProvider>().Object);

        var mockDispatcher = new Mock<IWorkDistributor>();
        mockDispatcher.Setup(d => d.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<(IssueIdentifier, ProviderConfigId)>());

        var svc = CreateService(mockStore, mockFactory, mockDispatcher.Object);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        var started = await svc.StartLoopAsync();
        if (!started) { cts.Cancel(); try { await svc.StopAsync(CancellationToken.None); } catch { } return; }

        // Wait for at least one poll cycle (generous delay for CI ARM runners under load)
        await Task.Delay(800);
        svc.StopLoop();
        await Task.Delay(200);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        // Property: decomposition polling occurs iff (Enabled AND DecompositionEnabled)
        var expectedPolling = enabled && decompositionEnabled;
        decompositionPolled.Should().Be(expectedPolling,
            $"Enabled={enabled}, DecompositionEnabled={decompositionEnabled}, " +
            $"ImplementationEnabled={implementationEnabled}, ReviewEnabled={reviewEnabled} — " +
            $"decomposition polling should{(expectedPolling ? "" : " NOT")} occur");
    }

    /// <summary>
    /// Feature: 027-epic-decomposition-pipeline, Property P2: Template Provider Validation
    /// For any template with DecompositionEnabled=true and a set of available provider configurations,
    /// the validation logic SHALL accept the template for decomposition polling if and only if both
    /// IssueProviderId and RepoProviderId reference existing provider configurations in the set.
    /// **Validates: Requirements 1.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task ProviderValidation_AcceptsIffBothProvidersExist(
        bool issueProviderExists, bool repoProviderExists)
    {
        var templateId = Guid.NewGuid().ToString();
        var issueProviderId = "ip-test";
        var repoProviderId = "rp-test";

        var template = new PipelineJobTemplate
        {
            Id = templateId,
            Name = "Test-Template",
            IssueProviderId = issueProviderId,
            RepoProviderId = repoProviderId,
            Enabled = true,
            ImplementationEnabled = false, // Disable to isolate decomposition behavior
            ReviewEnabled = false,
            DecompositionEnabled = true
        };

        var config = new PipelineConfiguration
        {
            ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50),
            ClosedLoopMaxRunsPerCycle = 10,
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = new List<string> { template.Id } }
            });
        mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        // Conditionally include provider configs based on test parameters
        var issueConfigs = issueProviderExists
            ? new List<ProviderConfig> { new() { Id = issueProviderId, Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" } }
            : new List<ProviderConfig>();
        mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfigs);

        var repoConfigs = repoProviderExists
            ? new List<ProviderConfig> { new() { Id = repoProviderId, Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" } }
            : new List<ProviderConfig>();
        mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfigs);

        var decompositionPolled = false;
        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns<ProviderConfig>(cfg =>
            {
                var mock = new Mock<IIssueProvider>();
                mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                        It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                    .Returns<int, int, IReadOnlyList<string>?, CancellationToken>((page, pageSize, labels, ct) =>
                    {
                        if (labels != null && (labels.Contains(AgentLabels.Epic) || labels.Contains(AgentLabels.EpicApproved)))
                        {
                            decompositionPolled = true;
                        }
                        return Task.FromResult(new PagedResult<IssueSummary>
                        {
                            Items = new List<IssueSummary>(),
                            Page = 1,
                            PageSize = PipelineConstants.DefaultPageSize,
                            HasMore = false
                        });
                    });
                return mock.Object;
            });
        mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(new Mock<IRepositoryProvider>().Object);

        var mockDispatcher = new Mock<IWorkDistributor>();
        mockDispatcher.Setup(d => d.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<(IssueIdentifier, ProviderConfigId)>());

        var svc = CreateService(mockStore, mockFactory, mockDispatcher.Object);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        var started = await svc.StartLoopAsync();
        if (!started) { cts.Cancel(); try { await svc.StopAsync(CancellationToken.None); } catch { } return; }

        // Wait for at least one poll cycle (generous delay for CI environments under load)
        await Task.Delay(800);
        svc.StopLoop();
        await Task.Delay(200);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        // Property: decomposition polling accepted iff both providers exist
        var expectedAccepted = issueProviderExists && repoProviderExists;
        decompositionPolled.Should().Be(expectedAccepted,
            $"IssueProviderExists={issueProviderExists}, RepoProviderExists={repoProviderExists} — " +
            $"decomposition polling should{(expectedAccepted ? "" : " NOT")} proceed");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static PipelineLoopService CreateService(
        Mock<IConfigurationStore> mockStore,
        Mock<IProviderFactory> mockFactory,
        IWorkDistributor? distributor = null)
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: mockStore.Object,
            providerFactory: mockFactory.Object,
            logger: mockLogger.Object);

        return new PipelineLoopService(runCreator, mockFactory.Object, mockStore.Object, mockStore.Object, mockStore.Object, mockLogger.Object, distributor);
    }
}
