using AwesomeAssertions;
using CodingAgentWebUI.Api.Dispatch;
using DispatchLifecycleService = CodingAgentWebUI.Api.Dispatch.DispatchLifecycleService;
using DispatchStateBuilder = CodingAgentWebUI.Api.Dispatch.DispatchStateBuilder;
using DispatchTemplateResolver = CodingAgentWebUI.Api.Dispatch.DispatchTemplateResolver;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="DispatchTemplateResolver.ResolveTemplateViaProfileAsync"/>.
/// Covers the profile-based fallback template resolution logic.
/// </summary>
public class DispatchTemplateResolverTests
{
    // ── null profile store ──────────────────────────────────────────────────

    [Fact]
    public async Task ResolveTemplateViaProfileAsync_WhenProfileStoreIsNull_ReturnsNullPair()
    {
        var resolver = new DispatchTemplateResolver(null, JobTemplateStore.CreateEmpty());

        var (template, selector) = await resolver.ResolveTemplateViaProfileAsync(
            "kiro,dotnet", "TestCaller", CancellationToken.None);

        template.Should().BeNull();
        selector.Should().BeNull();
    }

    // ── empty selector ──────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveTemplateViaProfileAsync_WhenSelectorIsEmpty_ReturnsNullPair()
    {
        var mockStore = new Mock<IAgentProfileStore>();
        var resolver = new DispatchTemplateResolver(mockStore.Object, JobTemplateStore.CreateEmpty());

        var (template, selector) = await resolver.ResolveTemplateViaProfileAsync(
            "", "TestCaller", CancellationToken.None);

        template.Should().BeNull();
        selector.Should().BeNull();
        // store should not be called for empty selector
        mockStore.Verify(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveTemplateViaProfileAsync_WhenSelectorIsWhitespaceOnly_ReturnsNullPair()
    {
        var mockStore = new Mock<IAgentProfileStore>();
        var resolver = new DispatchTemplateResolver(mockStore.Object, JobTemplateStore.CreateEmpty());

        var (template, selector) = await resolver.ResolveTemplateViaProfileAsync(
            "  ,  ", "TestCaller", CancellationToken.None);

        template.Should().BeNull();
        selector.Should().BeNull();
    }

    // ── profile not found ───────────────────────────────────────────────────

    [Fact]
    public async Task ResolveTemplateViaProfileAsync_WhenNoProfileMatchesSelector_ReturnsNullPair()
    {
        var mockStore = new Mock<IAgentProfileStore>();
        mockStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new AgentProfile
                {
                    DisplayName = "Python Profile",
                    AgentProviderConfigId = "agent-python",
                    MatchLabels = ["python"],
                    Enabled = true
                }
            });

        var resolver = new DispatchTemplateResolver(mockStore.Object, JobTemplateStore.CreateEmpty());

        // "kiro,dotnet" does not match "python" profile
        var (template, selector) = await resolver.ResolveTemplateViaProfileAsync(
            "kiro,dotnet", "TestCaller", CancellationToken.None);

        template.Should().BeNull();
        selector.Should().BeNull();
    }

    // ── profile found but no template ──────────────────────────────────────

    [Fact]
    public async Task ResolveTemplateViaProfileAsync_WhenProfileFoundButNoTemplate_ReturnsNullPair()
    {
        var mockStore = new Mock<IAgentProfileStore>();
        mockStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new AgentProfile
                {
                    DisplayName = "Kiro Profile",
                    AgentProviderConfigId = "agent-kiro",
                    MatchLabels = ["kiro", "dotnet"],
                    Enabled = true
                }
            });

        // Empty template store — no template for "dotnet,kiro"
        var resolver = new DispatchTemplateResolver(mockStore.Object, JobTemplateStore.CreateEmpty());

        var (template, selector) = await resolver.ResolveTemplateViaProfileAsync(
            "kiro", "TestCaller", CancellationToken.None);

        // When profile is found but no template matches the resolved selector,
        // the method returns (null, resolvedSelector) — not (null, null).
        template.Should().BeNull();
        selector.Should().Be("dotnet,kiro"); // sorted from profile.MatchLabels
    }

    // ── profile found and template resolved ────────────────────────────────

    [Fact]
    public async Task ResolveTemplateViaProfileAsync_WhenProfileAndTemplateFound_ReturnsTemplatAndSortedSelector()
    {
        var profile = new AgentProfile
        {
            DisplayName = "Kiro+Dotnet Profile",
            AgentProviderConfigId = "agent-kiro",
            MatchLabels = ["dotnet", "kiro"],
            Enabled = true
        };

        var mockStore = new Mock<IAgentProfileStore>();
        mockStore
            .Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile> { profile });

        // Build a store that has a template keyed to "dotnet,kiro" (alphabetical order).
        // "labels" is the JSON property name; "image" and "providerType" are required.
        var storeJson = """
            [
              {
                "labels": "dotnet,kiro",
                "image": "registry/coding-agent:kiro-dotnet",
                "providerType": "kiro"
              }
            ]
            """;
        var templateStore = JobTemplateStore.LoadFromJson(storeJson);
        var resolver = new DispatchTemplateResolver(mockStore.Object, templateStore);

        // "kiro" alone — profile matches (superset), resolved selector = "dotnet,kiro"
        var (template, selector) = await resolver.ResolveTemplateViaProfileAsync(
            "kiro", "TestCaller", CancellationToken.None);

        template.Should().NotBeNull();
        template!.ProviderType.Should().Be("kiro");
        selector.Should().Be("dotnet,kiro");
    }
}
