using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// End-to-end integration tests verifying the full label mapping procedure:
/// agent selection, profile resolution, and QGC resolution work correctly together.
/// </summary>
public class LabelMappingIntegrationTests
{
    private readonly ProfileResolver _profileResolver = new();
    private readonly QualityGateResolver _qgcResolver = new();

    #region Scenario 1: Basic .NET repo flow

    [Fact]
    public void BasicDotNetRepo_AgentWithMatchingLabels_IsSelected()
    {
        // Arrange: Repo has labels ["kiro", "dotnet", "dotnet10"]
        var requiredLabels = new List<string> { "kiro", "dotnet", "dotnet10" };
        var dispatcher = CreateDispatcherWithAgents(
            CreateAgent("agent-dotnet", ["kiro", "dotnet", "dotnet10"]),
            CreateAgent("agent-python", ["kiro", "python", "python312"]));

        // Act
        var selected = dispatcher.SelectAgent(requiredLabels);

        // Assert: Agent with superset labels is selected
        selected.Should().NotBeNull();
        selected!.AgentId.Should().Be("agent-dotnet");
    }

    [Fact]
    public void BasicDotNetRepo_AgentWithNonMatchingLabels_IsNotSelected()
    {
        // Arrange: Repo has labels ["kiro", "dotnet", "dotnet10"], only python agent available
        var requiredLabels = new List<string> { "kiro", "dotnet", "dotnet10" };
        var dispatcher = CreateDispatcherWithAgents(
            CreateAgent("agent-python", ["kiro", "python", "python312"]));

        // Act
        var selected = dispatcher.SelectAgent(requiredLabels);

        // Assert: Python agent does NOT match dotnet labels
        selected.Should().BeNull();
    }

    [Fact]
    public void BasicDotNetRepo_ProfileWithMatchingLabels_Resolves()
    {
        // Arrange: Agent has labels ["kiro", "dotnet", "dotnet10"]
        var agentLabels = new List<string> { "kiro", "dotnet", "dotnet10" };
        var profiles = new List<AgentProfile>
        {
            CreateProfile("profile-dotnet", "DotNet Profile", ["kiro", "dotnet", "dotnet10"]),
            CreateProfile("profile-python", "Python Profile", ["kiro", "python", "python312"])
        };

        // Act
        var resolved = _profileResolver.Resolve(profiles, agentLabels);

        // Assert
        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be("profile-dotnet");
    }

    [Fact]
    public void BasicDotNetRepo_QgcWithMatchingLabel_IsIncluded()
    {
        // Arrange: Job required labels ["kiro", "dotnet", "dotnet10"]
        var jobLabels = new List<string> { "kiro", "dotnet", "dotnet10" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-dotnet", "DotNet QGC", ["dotnet"]),
            CreateQgc("qgc-python", "Python QGC", ["python"])
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert: dotnet QGC included (intersection: "dotnet"), python QGC excluded
        resolved.Should().HaveCount(1);
        resolved[0].Id.Should().Be("qgc-dotnet");
    }

    [Fact]
    public void BasicDotNetRepo_QgcWithNonMatchingLabel_IsExcluded()
    {
        // Arrange
        var jobLabels = new List<string> { "kiro", "dotnet", "dotnet10" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-python", "Python QGC", ["python"])
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert
        resolved.Should().BeEmpty();
    }

    #endregion

    #region Scenario 2: Polyglot repo

    [Fact]
    public void PolyglotRepo_AgentWithAllLabels_IsSelected()
    {
        // Arrange: Repo has labels ["kiro", "dotnet", "python"]
        var requiredLabels = new List<string> { "kiro", "dotnet", "python" };
        var dispatcher = CreateDispatcherWithAgents(
            CreateAgent("agent-polyglot", ["kiro", "dotnet", "python", "dotnet10", "python312"]));

        // Act
        var selected = dispatcher.SelectAgent(requiredLabels);

        // Assert
        selected.Should().NotBeNull();
        selected!.AgentId.Should().Be("agent-polyglot");
    }

    [Fact]
    public void PolyglotRepo_BothStackQgcs_AreIncluded()
    {
        // Arrange: Job labels ["kiro", "dotnet", "python"]
        var jobLabels = new List<string> { "kiro", "dotnet", "python" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-dotnet", "DotNet QGC", ["dotnet"]),
            CreateQgc("qgc-python", "Python QGC", ["python"]),
            CreateQgc("qgc-java", "Java QGC", ["java"])
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert: dotnet and python included, java excluded
        resolved.Should().HaveCount(2);
        resolved.Select(q => q.Id).Should().Contain("qgc-dotnet");
        resolved.Select(q => q.Id).Should().Contain("qgc-python");
        resolved.Select(q => q.Id).Should().NotContain("qgc-java");
    }

    #endregion

    #region Scenario 3: Global fallback QGC

    [Fact]
    public void GlobalFallbackQgc_AlwaysIncluded_RegardlessOfJobLabels()
    {
        // Arrange: QGC with empty matchLabels is a global fallback
        var jobLabels = new List<string> { "kiro", "dotnet", "dotnet10" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-global", "Global Fallback", []),
            CreateQgc("qgc-dotnet", "DotNet QGC", ["dotnet"])
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert: Both global and stack-specific are included
        resolved.Should().HaveCount(2);
        resolved.Select(q => q.Id).Should().Contain("qgc-global");
        resolved.Select(q => q.Id).Should().Contain("qgc-dotnet");
    }

    [Fact]
    public void GlobalFallbackQgc_IncludedAlongsideStackSpecific_NotInsteadOf()
    {
        // Arrange: Global + dotnet + python QGCs, job has dotnet labels
        var jobLabels = new List<string> { "kiro", "dotnet" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-global", "Global Fallback", []),
            CreateQgc("qgc-dotnet", "DotNet QGC", ["dotnet"]),
            CreateQgc("qgc-python", "Python QGC", ["python"])
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert: Global AND dotnet included, python excluded
        resolved.Should().HaveCount(2);
        resolved.Select(q => q.Id).Should().Contain("qgc-global");
        resolved.Select(q => q.Id).Should().Contain("qgc-dotnet");
    }

    #endregion

    #region Scenario 4: Profile specificity

    [Fact]
    public void ProfileSpecificity_MostSpecificProfileWins()
    {
        // Arrange: Three profiles with increasing specificity
        var agentLabels = new List<string> { "kiro", "dotnet", "dotnet10" };
        var profiles = new List<AgentProfile>
        {
            CreateProfile("profile-a", "Profile A", ["kiro"]),                       // specificity 1
            CreateProfile("profile-b", "Profile B", ["kiro", "dotnet"]),             // specificity 2
            CreateProfile("profile-c", "Profile C", ["kiro", "dotnet", "dotnet10"]) // specificity 3
        };

        // Act
        var resolved = _profileResolver.Resolve(profiles, agentLabels);

        // Assert: Profile C wins (most specific — all 3 labels match)
        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be("profile-c");
    }

    [Fact]
    public void ProfileSpecificity_LessSpecificProfilesAlsoMatch_ButMostSpecificWins()
    {
        // Arrange: Agent has many labels, multiple profiles match
        var agentLabels = new List<string> { "kiro", "dotnet", "dotnet10", "linux" };
        var profiles = new List<AgentProfile>
        {
            CreateProfile("profile-generic", "Generic", ["kiro"]),
            CreateProfile("profile-dotnet", "DotNet", ["kiro", "dotnet"]),
            CreateProfile("profile-dotnet10", "DotNet 10", ["kiro", "dotnet", "dotnet10"])
        };

        // Act
        var resolved = _profileResolver.Resolve(profiles, agentLabels);

        // Assert: Most specific (3 labels) wins
        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be("profile-dotnet10");
    }

    #endregion

    #region Scenario 5: Disabled agent skipped

    [Fact]
    public void DisabledAgent_SkippedEvenThoughLabelsMatch()
    {
        // Arrange: Disabled agent matches labels, enabled agent also matches
        var requiredLabels = new List<string> { "kiro", "dotnet" };
        var disabledAgent = CreateAgent("agent-disabled", ["kiro", "dotnet", "dotnet10"]);
        disabledAgent.Disabled = true;
        var enabledAgent = CreateAgent("agent-enabled", ["kiro", "dotnet", "dotnet10"]);

        var dispatcher = CreateDispatcherWithAgents(disabledAgent, enabledAgent);

        // Act
        var selected = dispatcher.SelectAgent(requiredLabels);

        // Assert: Disabled agent skipped, enabled agent selected
        selected.Should().NotBeNull();
        selected!.AgentId.Should().Be("agent-enabled");
    }

    [Fact]
    public void DisabledAgent_OnlyCompatibleAgent_ReturnsNull()
    {
        // Arrange: Only agent that matches is disabled
        var requiredLabels = new List<string> { "kiro", "dotnet" };
        var disabledAgent = CreateAgent("agent-disabled", ["kiro", "dotnet", "dotnet10"]);
        disabledAgent.Disabled = true;

        var dispatcher = CreateDispatcherWithAgents(disabledAgent);

        // Act
        var selected = dispatcher.SelectAgent(requiredLabels);

        // Assert: No agent available
        selected.Should().BeNull();
    }

    #endregion

    #region Scenario 6: No profile matches → dispatch fails

    [Fact]
    public void NoProfileMatches_ReturnsNull()
    {
        // Arrange: Agent has labels ["kiro", "dotnet10"], only profile requires ["kiro", "python"]
        var agentLabels = new List<string> { "kiro", "dotnet10" };
        var profiles = new List<AgentProfile>
        {
            CreateProfile("profile-python", "Python Profile", ["kiro", "python"])
        };

        // Act
        var resolved = _profileResolver.Resolve(profiles, agentLabels);

        // Assert: No profile matches (["kiro", "python"] is NOT a subset of ["kiro", "dotnet10"])
        resolved.Should().BeNull();
    }

    [Fact]
    public void NoProfileMatches_DisabledProfileAlsoExcluded()
    {
        // Arrange: Matching profile exists but is disabled
        var agentLabels = new List<string> { "kiro", "dotnet", "dotnet10" };
        var profiles = new List<AgentProfile>
        {
            new()
            {
                Id = "profile-disabled",
                DisplayName = "Disabled Profile",
                MatchLabels = ["kiro", "dotnet", "dotnet10"],
                AgentProviderConfigId = "ap-1",
                Enabled = false
            }
        };

        // Act
        var resolved = _profileResolver.Resolve(profiles, agentLabels);

        // Assert
        resolved.Should().BeNull();
    }

    #endregion

    #region Scenario 7: RequiredLabels from ProviderConfig.RequiredLabels field

    [Fact]
    public void RequiredLabels_ExplicitField_TakesPrecedenceOverSettings()
    {
        // Arrange: ProviderConfig with explicit RequiredLabels AND Settings dictionary entry
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            RequiredLabels = new List<string> { "kiro", "dotnet", "dotnet10" },
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.RequiredAgentLabels] = "kiro,python"
            }
        };
        var pipelineConfig = new PipelineConfiguration();

        // Act
        var resolved = JobDispatcherService.ResolveRequiredLabels(repoConfig, pipelineConfig);

        // Assert: Explicit RequiredLabels wins over Settings dictionary
        resolved.Should().BeEquivalentTo(new[] { "kiro", "dotnet", "dotnet10" });
    }

    [Fact]
    public void RequiredLabels_ExplicitField_UsedForResolution()
    {
        // Arrange
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            RequiredLabels = new List<string> { "kiro", "dotnet", "dotnet10" }
        };
        var pipelineConfig = new PipelineConfiguration();

        // Act
        var labels = JobDispatcherService.ResolveRequiredLabels(repoConfig, pipelineConfig);

        // Assert
        labels.Should().HaveCount(3);
        labels.Should().Contain("kiro");
        labels.Should().Contain("dotnet");
        labels.Should().Contain("dotnet10");
    }

    #endregion

    #region Scenario 8: RequiredLabels fallback to Settings dictionary

    [Fact]
    public void RequiredLabels_FallsBackToSettingsDictionary_WhenExplicitFieldNull()
    {
        // Arrange: RequiredLabels is null, Settings has requiredAgentLabels
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            RequiredLabels = null,
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.RequiredAgentLabels] = "kiro,dotnet"
            }
        };
        var pipelineConfig = new PipelineConfiguration();

        // Act
        var resolved = JobDispatcherService.ResolveRequiredLabels(repoConfig, pipelineConfig);

        // Assert: Falls back to Settings dictionary parsing
        resolved.Should().BeEquivalentTo(new[] { "kiro", "dotnet" });
    }

    [Fact]
    public void RequiredLabels_FallsBackToPipelineDefault_WhenBothNull()
    {
        // Arrange: No RequiredLabels, no Settings entry, but pipeline has default
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            RequiredLabels = null
        };
        var pipelineConfig = new PipelineConfiguration
        {
            DefaultRequiredAgentLabels = "kiro,dotnet"
        };

        // Act
        var resolved = JobDispatcherService.ResolveRequiredLabels(repoConfig, pipelineConfig);

        // Assert: Falls back to pipeline default
        resolved.Should().BeEquivalentTo(new[] { "kiro", "dotnet" });
    }

    [Fact]
    public void RequiredLabels_ReturnsEmpty_WhenNothingConfigured()
    {
        // Arrange: No labels configured anywhere
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo"
        };
        var pipelineConfig = new PipelineConfiguration();

        // Act
        var resolved = JobDispatcherService.ResolveRequiredLabels(repoConfig, pipelineConfig);

        // Assert: Empty — any agent matches
        resolved.Should().BeEmpty();
    }

    #endregion

    #region Scenario 9: QGC execution order

    [Fact]
    public void QgcExecutionOrder_ResolvedInCorrectOrder()
    {
        // Arrange: Multiple QGCs with different execution orders
        var jobLabels = new List<string> { "kiro", "dotnet" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-security", "Security", ["kiro"], executionOrder: 10),
            CreateQgc("qgc-dotnet", "DotNet", ["dotnet"], executionOrder: 0),
            CreateQgc("qgc-global", "Global Fallback", [], executionOrder: 5)
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert: Ordered by ExecutionOrder: dotnet (0), global (5), security (10)
        resolved.Should().HaveCount(3);
        resolved[0].Id.Should().Be("qgc-dotnet");
        resolved[0].ExecutionOrder.Should().Be(0);
        resolved[1].Id.Should().Be("qgc-global");
        resolved[1].ExecutionOrder.Should().Be(5);
        resolved[2].Id.Should().Be("qgc-security");
        resolved[2].ExecutionOrder.Should().Be(10);
    }

    [Fact]
    public void QgcExecutionOrder_SameOrder_SortedByDisplayNameAlphabetically()
    {
        // Arrange: Two QGCs with same execution order
        var jobLabels = new List<string> { "kiro", "dotnet" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-z", "Zebra QGC", ["dotnet"], executionOrder: 0),
            CreateQgc("qgc-a", "Alpha QGC", ["dotnet"], executionOrder: 0)
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert: Alphabetical by DisplayName when ExecutionOrder is tied
        resolved.Should().HaveCount(2);
        resolved[0].DisplayName.Should().Be("Alpha QGC");
        resolved[1].DisplayName.Should().Be("Zebra QGC");
    }

    #endregion

    #region Scenario 10: Case insensitivity

    [Fact]
    public void CaseInsensitivity_AgentSelection_MatchesRegardlessOfCase()
    {
        // Arrange: Repo labels with mixed case
        var requiredLabels = new List<string> { "Kiro", "DotNet" };
        var dispatcher = CreateDispatcherWithAgents(
            CreateAgent("agent-dotnet", ["kiro", "dotnet", "dotnet10"]));

        // Act
        var selected = dispatcher.SelectAgent(requiredLabels);

        // Assert: Case-insensitive match succeeds
        selected.Should().NotBeNull();
        selected!.AgentId.Should().Be("agent-dotnet");
    }

    [Fact]
    public void CaseInsensitivity_ProfileResolution_MatchesRegardlessOfCase()
    {
        // Arrange: Profile matchLabels in UPPERCASE, agent labels in lowercase
        var agentLabels = new List<string> { "kiro", "dotnet", "dotnet10" };
        var profiles = new List<AgentProfile>
        {
            CreateProfile("profile-upper", "Upper Profile", ["KIRO", "DOTNET", "DOTNET10"])
        };

        // Act
        var resolved = _profileResolver.Resolve(profiles, agentLabels);

        // Assert: Case-insensitive match
        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be("profile-upper");
    }

    [Fact]
    public void CaseInsensitivity_QgcResolution_MatchesRegardlessOfCase()
    {
        // Arrange: QGC matchLabels lowercase, job labels mixed case
        var jobLabels = new List<string> { "Kiro", "DotNet" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-dotnet", "DotNet QGC", ["dotnet"])
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert: Case-insensitive intersection
        resolved.Should().HaveCount(1);
        resolved[0].Id.Should().Be("qgc-dotnet");
    }

    [Fact]
    public void CaseInsensitivity_FullFlow_AllSystemsMatchCorrectly()
    {
        // Arrange: Mixed case throughout the entire flow
        var repoLabels = new List<string> { "Kiro", "DotNet" };
        var agentLabels = new List<string> { "kiro", "dotnet", "dotnet10" };

        // Agent selection
        var dispatcher = CreateDispatcherWithAgents(
            CreateAgent("agent-dotnet", agentLabels));
        var selectedAgent = dispatcher.SelectAgent(repoLabels);

        // Profile resolution
        var profiles = new List<AgentProfile>
        {
            CreateProfile("profile-dotnet", "DotNet", ["KIRO", "DOTNET"])
        };
        var resolvedProfile = _profileResolver.Resolve(profiles, agentLabels);

        // QGC resolution
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-dotnet", "DotNet QGC", ["dotnet"])
        };
        var resolvedQgcs = _qgcResolver.Resolve(qgcs, repoLabels);

        // Assert: All three systems match correctly despite mixed case
        selectedAgent.Should().NotBeNull();
        resolvedProfile.Should().NotBeNull();
        resolvedQgcs.Should().HaveCount(1);
    }

    #endregion

    #region Helpers

    private static AgentEntry CreateAgent(string agentId, IReadOnlyList<string> labels)
    {
        return new AgentEntry
        {
            AgentId = agentId,
            ConnectionId = $"conn-{agentId}",
            Hostname = "test-host",
            AgentType = "kiro-test",
            Labels = labels,
            Status = AgentStatus.Idle,
            RegisteredAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastHeartbeatAt = DateTimeOffset.UtcNow
        };
    }

    private static AgentProfile CreateProfile(string id, string displayName, IReadOnlyList<string> matchLabels)
    {
        return new AgentProfile
        {
            Id = id,
            DisplayName = displayName,
            MatchLabels = matchLabels,
            AgentProviderConfigId = $"ap-{id}",
            Enabled = true,
            Priority = 0
        };
    }

    private static QualityGateConfiguration CreateQgc(
        string id, string displayName, IReadOnlyList<string> matchLabels, int executionOrder = 0)
    {
        return new QualityGateConfiguration
        {
            Id = id,
            DisplayName = displayName,
            MatchLabels = matchLabels,
            CompilationCommand = "dotnet",
            CompilationArguments = ["build"],
            TestCommand = "dotnet",
            TestArguments = ["test"],
            Enabled = true,
            ExecutionOrder = executionOrder
        };
    }

    private static JobDispatcherService CreateDispatcherWithAgents(params AgentEntry[] agents)
    {
        var mockLogger = new Mock<ILogger>();
        var registry = new AgentRegistryService(mockLogger.Object);

        // Register agents via the registry
        foreach (var agent in agents)
        {
            var registrationMsg = new AgentRegistrationMessage
            {
                AgentId = agent.AgentId,
                Hostname = agent.Hostname,
                AgentType = agent.AgentType,
                Labels = agent.Labels
            };
            var entry = registry.Register(registrationMsg, agent.ConnectionId);
            entry.Disabled = agent.Disabled;
        }

        return new JobDispatcherService(registry, mockLogger.Object);
    }

    #endregion
}
