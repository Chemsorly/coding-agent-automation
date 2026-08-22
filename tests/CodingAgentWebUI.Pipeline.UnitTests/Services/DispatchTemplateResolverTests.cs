using AwesomeAssertions;
using CodingAgentWebUI.Api.Dispatch;
using DispatchTemplateResolver = CodingAgentWebUI.Api.Dispatch.DispatchTemplateResolver;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Characterization tests for <see cref="DispatchTemplateResolver"/>.
/// Validates the extracted ResolveTemplateViaProfileAsync logic matches the original behavior.
/// Issue #1630 prerequisite: characterization tests before extraction.
/// </summary>
public class DispatchTemplateResolverTests
{
    private readonly Mock<IAgentProfileStore> _mockProfileStore;

    public DispatchTemplateResolverTests()
    {
        _mockProfileStore = new Mock<IAgentProfileStore>();
    }

    [Fact]
    public async Task ResolveTemplateViaProfileAsync_NullProfileStore_ReturnsNull()
    {
        // Arrange
        var templateProvider = BuildTemplateProvider("kiro,dotnet,dotnet10");
        var resolver = new DispatchTemplateResolver(agentProfileStore: null, templateProvider);

        // Act
        var (template, selector) = await resolver.ResolveTemplateViaProfileAsync("dotnet,dotnet10", "TestCaller", CancellationToken.None);

        // Assert
        template.Should().BeNull();
        selector.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTemplateViaProfileAsync_EmptySelector_ReturnsNull()
    {
        // Arrange
        var templateProvider = BuildTemplateProvider("kiro,dotnet,dotnet10");
        var resolver = new DispatchTemplateResolver(_mockProfileStore.Object, templateProvider);

        // Act
        var (template, selector) = await resolver.ResolveTemplateViaProfileAsync("", "TestCaller", CancellationToken.None);

        // Assert
        template.Should().BeNull();
        selector.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTemplateViaProfileAsync_WhitespaceOnlySelector_ReturnsNull()
    {
        // Arrange
        var templateProvider = BuildTemplateProvider("kiro,dotnet,dotnet10");
        var resolver = new DispatchTemplateResolver(_mockProfileStore.Object, templateProvider);

        // Act
        var (template, selector) = await resolver.ResolveTemplateViaProfileAsync("  ,  , ", "TestCaller", CancellationToken.None);

        // Assert
        template.Should().BeNull();
        selector.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTemplateViaProfileAsync_NoProfileCoversSelector_ReturnsNull()
    {
        // Arrange
        var profiles = new List<AgentProfile>
        {
            CreateProfile("profile-1", ["kiro", "python", "python312"])
        };
        _mockProfileStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles);

        var templateProvider = BuildTemplateProvider("kiro,dotnet,dotnet10");
        var resolver = new DispatchTemplateResolver(_mockProfileStore.Object, templateProvider);

        // Act — "dotnet,dotnet10" is not covered by the python profile
        var (template, selector) = await resolver.ResolveTemplateViaProfileAsync("dotnet,dotnet10", "TestCaller", CancellationToken.None);

        // Assert
        template.Should().BeNull();
        selector.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTemplateViaProfileAsync_ProfileMatchesAndTemplateResolves_ReturnsTemplateAndSelector()
    {
        // Arrange — profile has ["kiro", "dotnet", "dotnet10"], template keyed on "dotnet,dotnet10,kiro"
        var profiles = new List<AgentProfile>
        {
            CreateProfile("profile-dotnet", ["kiro", "dotnet", "dotnet10"])
        };
        _mockProfileStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles);

        var templateProvider = BuildTemplateProvider("kiro,dotnet,dotnet10");
        var resolver = new DispatchTemplateResolver(_mockProfileStore.Object, templateProvider);

        // Act — subset selector "dotnet,dotnet10" should resolve to full profile selector
        var (template, resolvedSelector) = await resolver.ResolveTemplateViaProfileAsync("dotnet,dotnet10", "TestCaller", CancellationToken.None);

        // Assert
        template.Should().NotBeNull();
        resolvedSelector.Should().Be("dotnet,dotnet10,kiro"); // sorted alphabetically
    }

    [Fact]
    public async Task ResolveTemplateViaProfileAsync_ProfileMatchesButTemplateDoesNotResolve_ReturnsNullTemplate()
    {
        // Arrange — profile has ["kiro", "dotnet", "dotnet10"] but template is keyed on "opencode,python,python312"
        var profiles = new List<AgentProfile>
        {
            CreateProfile("profile-dotnet", ["kiro", "dotnet", "dotnet10"])
        };
        _mockProfileStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles);

        var templateProvider = BuildTemplateProvider("opencode,python,python312");
        var resolver = new DispatchTemplateResolver(_mockProfileStore.Object, templateProvider);

        // Act
        var (template, resolvedSelector) = await resolver.ResolveTemplateViaProfileAsync("dotnet,dotnet10", "TestCaller", CancellationToken.None);

        // Assert — profile matches so profileSelector is computed, but template lookup fails
        template.Should().BeNull();
        resolvedSelector.Should().Be("dotnet,dotnet10,kiro"); // selector is still returned
    }

    [Fact]
    public async Task ResolveTemplateViaProfileAsync_SelectorLabelsAreTrimmedAndEmptyRemoved()
    {
        // Arrange
        var profiles = new List<AgentProfile>
        {
            CreateProfile("profile-dotnet", ["kiro", "dotnet", "dotnet10"])
        };
        _mockProfileStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles);

        var templateProvider = BuildTemplateProvider("kiro,dotnet,dotnet10");
        var resolver = new DispatchTemplateResolver(_mockProfileStore.Object, templateProvider);

        // Act — whitespace around labels and empty segments
        var (template, resolvedSelector) = await resolver.ResolveTemplateViaProfileAsync(" dotnet , dotnet10 ,", "TestCaller", CancellationToken.None);

        // Assert — should still resolve correctly
        template.Should().NotBeNull();
        resolvedSelector.Should().Be("dotnet,dotnet10,kiro");
    }

    [Fact]
    public async Task ResolveTemplateViaProfileAsync_ProfileMatchLabelsAreSortedAndJoinedWithComma()
    {
        // Arrange — profile labels in non-sorted order
        var profiles = new List<AgentProfile>
        {
            CreateProfile("profile-dotnet", ["dotnet10", "kiro", "dotnet"]) // intentionally unsorted
        };
        _mockProfileStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles);

        var templateProvider = BuildTemplateProvider("dotnet,dotnet10,kiro"); // sorted key
        var resolver = new DispatchTemplateResolver(_mockProfileStore.Object, templateProvider);

        // Act
        var (template, resolvedSelector) = await resolver.ResolveTemplateViaProfileAsync("dotnet10,kiro", "TestCaller", CancellationToken.None);

        // Assert — should resolve with sorted selector regardless of profile label order
        template.Should().NotBeNull();
        resolvedSelector.Should().Be("dotnet,dotnet10,kiro");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static JobTemplateStore BuildTemplateProvider(string labels)
    {
        var templates = new List<JobTemplate>
        {
            new() { Labels = labels, Image = "ghcr.io/agent:latest", ProviderType = "kiro" }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(templates);
        return JobTemplateStore.LoadFromJson(json);
    }

    private static AgentProfile CreateProfile(string id, IReadOnlyList<string> matchLabels)
    {
        return new AgentProfile
        {
            Id = id,
            DisplayName = $"Profile {id}",
            MatchLabels = matchLabels,
            AgentProviderConfigId = $"ap-{id}",
            Enabled = true,
            Priority = 0
        };
    }
}
