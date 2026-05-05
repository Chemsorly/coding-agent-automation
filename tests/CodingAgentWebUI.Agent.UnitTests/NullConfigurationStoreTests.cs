using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="NullConfigurationStore"/>.
/// Verifies all Load methods return empty/default values and all Save/Delete methods complete without throwing.
/// Requirements: 7.1, 7.2, 7.3, 7.4, 7.5, 7.6
/// </summary>
public class NullConfigurationStoreTests
{
    private readonly NullConfigurationStore _sut = new();

    // ── LoadPipelineConfigAsync ──────────────────────────────────────────

    [Fact]
    public async Task LoadPipelineConfigAsync_ReturnsDefaultPipelineConfiguration()
    {
        // Act
        var result = await _sut.LoadPipelineConfigAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new PipelineConfiguration());
    }

    // ── LoadProviderConfigsAsync ─────────────────────────────────────────

    [Theory]
    [InlineData(ProviderKind.Issue)]
    [InlineData(ProviderKind.Repository)]
    [InlineData(ProviderKind.Agent)]
    [InlineData(ProviderKind.Pipeline)]
    public async Task LoadProviderConfigsAsync_AnyProviderKind_ReturnsEmptyList(ProviderKind kind)
    {
        // Act
        var result = await _sut.LoadProviderConfigsAsync(kind, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    // ── LoadAgentProfilesAsync ───────────────────────────────────────────

    [Fact]
    public async Task LoadAgentProfilesAsync_ReturnsEmptyList()
    {
        // Act
        var result = await _sut.LoadAgentProfilesAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    // ── LoadQualityGateConfigsAsync ──────────────────────────────────────

    [Fact]
    public async Task LoadQualityGateConfigsAsync_ReturnsEmptyList()
    {
        // Act
        var result = await _sut.LoadQualityGateConfigsAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    // ── LoadReviewerConfigsAsync ─────────────────────────────────────────

    [Fact]
    public async Task LoadReviewerConfigsAsync_ReturnsEmptyList()
    {
        // Act
        var result = await _sut.LoadReviewerConfigsAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    // ── Save and Delete methods complete without throwing ─────────────────

    [Fact]
    public async Task SavePipelineConfigAsync_CompletesWithoutThrowing()
    {
        // Act
        var act = () => _sut.SavePipelineConfigAsync(new PipelineConfiguration(), CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UpdatePipelineConfigAsync_CompletesWithoutThrowing()
    {
        // Act
        var act = () => _sut.UpdatePipelineConfigAsync(c => c, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaveProviderConfigAsync_CompletesWithoutThrowing()
    {
        // Arrange
        var config = new ProviderConfig
        {
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Test Provider"
        };

        // Act
        var act = () => _sut.SaveProviderConfigAsync(config, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteProviderConfigAsync_CompletesWithoutThrowing()
    {
        // Act
        var act = () => _sut.DeleteProviderConfigAsync("some-id", ProviderKind.Repository, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaveAgentProfileAsync_CompletesWithoutThrowing()
    {
        // Arrange
        var profile = new AgentProfile
        {
            DisplayName = "Test Profile",
            AgentProviderConfigId = "agent-config-1"
        };

        // Act
        var act = () => _sut.SaveAgentProfileAsync(profile, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteAgentProfileAsync_CompletesWithoutThrowing()
    {
        // Act
        var act = () => _sut.DeleteAgentProfileAsync("some-id", CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaveQualityGateConfigAsync_CompletesWithoutThrowing()
    {
        // Arrange
        var config = new QualityGateConfiguration { DisplayName = "Test QG" };

        // Act
        var act = () => _sut.SaveQualityGateConfigAsync(config, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteQualityGateConfigAsync_CompletesWithoutThrowing()
    {
        // Act
        var act = () => _sut.DeleteQualityGateConfigAsync("some-id", CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SaveReviewerConfigAsync_CompletesWithoutThrowing()
    {
        // Arrange
        var config = new ReviewerConfiguration
        {
            DisplayName = "Test Reviewer",
            Agents = [new ReviewAgent { Name = "TestAgent", Prompt = "Review this" }]
        };

        // Act
        var act = () => _sut.SaveReviewerConfigAsync(config, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteReviewerConfigAsync_CompletesWithoutThrowing()
    {
        // Act
        var act = () => _sut.DeleteReviewerConfigAsync("some-id", CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
    }

    // ── Property 3: NullConfigurationStore returns empty for all ProviderKinds ──

    /// <summary>
    /// Feature: 019-unit-test-coverage-improvement, Property 3: NullConfigurationStore returns empty for all ProviderKinds
    /// For any ProviderKind enum value, calling NullConfigurationStore.LoadProviderConfigsAsync
    /// SHALL return an empty list.
    /// **Validates: Requirements 7.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public bool LoadProviderConfigsAsync_AnyProviderKind_AlwaysReturnsEmpty(ProviderKind kind)
    {
        // Arrange
        var store = new NullConfigurationStore();

        // Act
        var result = store.LoadProviderConfigsAsync(kind, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Assert
        return result is not null && result.Count == 0;
    }

    // ── Property 4: NullConfigurationStore Save/Delete operations never throw ──

    /// <summary>
    /// Feature: 019-unit-test-coverage-improvement, Property 4: NullConfigurationStore Save/Delete operations never throw
    /// For any valid PipelineConfiguration, Save completes without exception.
    /// **Validates: Requirements 7.6**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(NullConfigStoreArbitrary)])]
    public bool SavePipelineConfigAsync_AnyConfig_NeverThrows(PipelineConfiguration config)
    {
        // Arrange
        var store = new NullConfigurationStore();

        // Act & Assert
        try
        {
            store.SavePipelineConfigAsync(config, CancellationToken.None)
                .GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Feature: 019-unit-test-coverage-improvement, Property 4: NullConfigurationStore Save/Delete operations never throw
    /// For any valid ProviderConfig, Save completes without exception.
    /// **Validates: Requirements 7.6**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(NullConfigStoreArbitrary)])]
    public bool SaveProviderConfigAsync_AnyConfig_NeverThrows(ProviderConfig config)
    {
        // Arrange
        var store = new NullConfigurationStore();

        // Act & Assert
        try
        {
            store.SaveProviderConfigAsync(config, CancellationToken.None)
                .GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Feature: 019-unit-test-coverage-improvement, Property 4: NullConfigurationStore Save/Delete operations never throw
    /// For any valid AgentProfile, Save completes without exception.
    /// **Validates: Requirements 7.6**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(NullConfigStoreArbitrary)])]
    public bool SaveAgentProfileAsync_AnyProfile_NeverThrows(AgentProfile profile)
    {
        // Arrange
        var store = new NullConfigurationStore();

        // Act & Assert
        try
        {
            store.SaveAgentProfileAsync(profile, CancellationToken.None)
                .GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Feature: 019-unit-test-coverage-improvement, Property 4: NullConfigurationStore Save/Delete operations never throw
    /// For any valid QualityGateConfiguration, Save completes without exception.
    /// **Validates: Requirements 7.6**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(NullConfigStoreArbitrary)])]
    public bool SaveQualityGateConfigAsync_AnyConfig_NeverThrows(QualityGateConfiguration config)
    {
        // Arrange
        var store = new NullConfigurationStore();

        // Act & Assert
        try
        {
            store.SaveQualityGateConfigAsync(config, CancellationToken.None)
                .GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Feature: 019-unit-test-coverage-improvement, Property 4: NullConfigurationStore Save/Delete operations never throw
    /// For any valid ReviewerConfiguration, Save completes without exception.
    /// **Validates: Requirements 7.6**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(NullConfigStoreArbitrary)])]
    public bool SaveReviewerConfigAsync_AnyConfig_NeverThrows(ReviewerConfiguration config)
    {
        // Arrange
        var store = new NullConfigurationStore();

        // Act & Assert
        try
        {
            store.SaveReviewerConfigAsync(config, CancellationToken.None)
                .GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Feature: 019-unit-test-coverage-improvement, Property 4: NullConfigurationStore Save/Delete operations never throw
    /// For any string ID and ProviderKind, Delete completes without exception.
    /// **Validates: Requirements 7.6**
    /// </summary>
    [Property(MaxTest = 20)]
    public bool DeleteProviderConfigAsync_AnyIdAndKind_NeverThrows(NonNull<string> id, ProviderKind kind)
    {
        // Arrange
        var store = new NullConfigurationStore();

        // Act & Assert
        try
        {
            store.DeleteProviderConfigAsync(id.Get, kind, CancellationToken.None)
                .GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Feature: 019-unit-test-coverage-improvement, Property 4: NullConfigurationStore Save/Delete operations never throw
    /// For any string ID, DeleteAgentProfile completes without exception.
    /// **Validates: Requirements 7.6**
    /// </summary>
    [Property(MaxTest = 20)]
    public bool DeleteAgentProfileAsync_AnyId_NeverThrows(NonNull<string> id)
    {
        // Arrange
        var store = new NullConfigurationStore();

        // Act & Assert
        try
        {
            store.DeleteAgentProfileAsync(id.Get, CancellationToken.None)
                .GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Feature: 019-unit-test-coverage-improvement, Property 4: NullConfigurationStore Save/Delete operations never throw
    /// For any string ID, DeleteQualityGateConfig completes without exception.
    /// **Validates: Requirements 7.6**
    /// </summary>
    [Property(MaxTest = 20)]
    public bool DeleteQualityGateConfigAsync_AnyId_NeverThrows(NonNull<string> id)
    {
        // Arrange
        var store = new NullConfigurationStore();

        // Act & Assert
        try
        {
            store.DeleteQualityGateConfigAsync(id.Get, CancellationToken.None)
                .GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Feature: 019-unit-test-coverage-improvement, Property 4: NullConfigurationStore Save/Delete operations never throw
    /// For any string ID, DeleteReviewerConfig completes without exception.
    /// **Validates: Requirements 7.6**
    /// </summary>
    [Property(MaxTest = 20)]
    public bool DeleteReviewerConfigAsync_AnyId_NeverThrows(NonNull<string> id)
    {
        // Arrange
        var store = new NullConfigurationStore();

        // Act & Assert
        try
        {
            store.DeleteReviewerConfigAsync(id.Get, CancellationToken.None)
                .GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// FsCheck arbitraries for NullConfigurationStore property tests.
/// Generates valid configuration objects for Save/Delete property testing.
/// </summary>
public static class NullConfigStoreArbitrary
{
    private static readonly string[] DisplayNames =
    [
        "Default", "Production", "Staging", "Test", "Dev",
        "Agent-1", "QG-Main", "Reviewer-Security", "Pipeline-CI"
    ];

    private static readonly string[] ProviderTypes =
    [
        "GitHub", "KiroCli", "GitLab", "AzureDevOps"
    ];

    public static Arbitrary<PipelineConfiguration> PipelineConfigurations()
    {
        var gen =
            from maxRetries in Gen.Choose(1, 10)
            from timeout in Gen.Choose(1, 60)
            select new PipelineConfiguration
            {
                MaxRetries = maxRetries,
                AgentTimeout = TimeSpan.FromMinutes(timeout)
            };

        return gen.ToArbitrary();
    }

    public static Arbitrary<ProviderConfig> ProviderConfigs()
    {
        var gen =
            from kind in Gen.Elements(Enum.GetValues<ProviderKind>())
            from providerType in Gen.Elements(ProviderTypes)
            from displayName in Gen.Elements(DisplayNames)
            select new ProviderConfig
            {
                Kind = kind,
                ProviderType = providerType,
                DisplayName = displayName
            };

        return gen.ToArbitrary();
    }

    public static Arbitrary<AgentProfile> AgentProfiles()
    {
        var gen =
            from displayName in Gen.Elements(DisplayNames)
            from agentConfigId in Gen.Elements("config-1", "config-2", "config-3")
            from enabled in Gen.Elements(true, false)
            from priority in Gen.Choose(0, 10)
            select new AgentProfile
            {
                DisplayName = displayName,
                AgentProviderConfigId = agentConfigId,
                Enabled = enabled,
                Priority = priority
            };

        return gen.ToArbitrary();
    }

    public static Arbitrary<QualityGateConfiguration> QualityGateConfigurations()
    {
        var gen =
            from displayName in Gen.Elements(DisplayNames)
            from enabled in Gen.Elements(true, false)
            from order in Gen.Choose(0, 5)
            select new QualityGateConfiguration
            {
                DisplayName = displayName,
                Enabled = enabled,
                ExecutionOrder = order
            };

        return gen.ToArbitrary();
    }

    public static Arbitrary<ReviewerConfiguration> ReviewerConfigurations()
    {
        var gen =
            from displayName in Gen.Elements(DisplayNames)
            from agentCount in Gen.Choose(1, 3)
            from agents in Gen.ArrayOf(ReviewAgentGen(), agentCount)
            from enabled in Gen.Elements(true, false)
            select new ReviewerConfiguration
            {
                DisplayName = displayName,
                Agents = agents.ToList(),
                Enabled = enabled
            };

        return gen.ToArbitrary();
    }

    private static Gen<ReviewAgent> ReviewAgentGen()
    {
        return
            from name in Gen.Elements("Correctness", "Security", "Performance", "Style")
            from prompt in Gen.Elements("Review for correctness", "Check security", "Analyze performance")
            select new ReviewAgent { Name = name, Prompt = prompt };
    }
}
