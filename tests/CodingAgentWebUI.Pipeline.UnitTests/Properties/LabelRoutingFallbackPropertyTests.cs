using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for label routing fallback resolution and agent matching.
/// </summary>
public class LabelRoutingFallbackPropertyTests
{
    /// <summary>
    /// Property 23: Label Routing Fallback
    /// When repo config has requiredAgentLabels, those are used.
    /// **Validates: Requirements 19.3**
    /// </summary>
    [Property(MaxTest = 100)]
    public void ResolveLabels_UsesRepoLabels_WhenPresent(NonEmptyString label1, NonEmptyString label2)
    {
        var l1 = label1.Get.Replace(",", "").Trim();
        var l2 = label2.Get.Replace(",", "").Trim();
        if (string.IsNullOrEmpty(l1) || string.IsNullOrEmpty(l2)) return;

        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingsKeys.RequiredAgentLabels] = $"{l1},{l2}"
            }
        };

        var pipelineConfig = new PipelineConfiguration
        {
            DefaultRequiredAgentLabels = "fallback-label"
        };

        var resolved = JobDispatcherService.ResolveRequiredLabels(repoConfig, pipelineConfig);

        resolved.Should().Contain(l1);
        resolved.Should().Contain(l2);
        resolved.Should().NotContain("fallback-label");
    }

    /// <summary>
    /// Property 23 (continued): When repo config has no requiredAgentLabels,
    /// falls back to DefaultRequiredAgentLabels.
    /// **Validates: Requirements 19.3**
    /// </summary>
    [Property(MaxTest = 100)]
    public void ResolveLabels_FallsBackToDefault_WhenRepoHasNoLabels(NonEmptyString defaultLabel)
    {
        var label = defaultLabel.Get.Replace(",", "").Trim();
        if (string.IsNullOrEmpty(label)) return;

        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test"
        };

        var pipelineConfig = new PipelineConfiguration
        {
            DefaultRequiredAgentLabels = label
        };

        var resolved = JobDispatcherService.ResolveRequiredLabels(repoConfig, pipelineConfig);

        resolved.Should().Contain(label);
    }

    /// <summary>
    /// Property 23 (continued): When neither repo nor default labels are set, resolves to empty.
    /// **Validates: Requirements 19.3**
    /// </summary>
    [Fact]
    public void ResolveLabels_ReturnsEmpty_WhenNoLabelsConfigured()
    {
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test"
        };

        var pipelineConfig = new PipelineConfiguration();

        var resolved = JobDispatcherService.ResolveRequiredLabels(repoConfig, pipelineConfig);

        resolved.Should().BeEmpty();
    }

    /// <summary>
    /// Property 23 (continued): Agent matches iff its labels are a superset of resolved required labels.
    /// **Validates: Requirements 19.3**
    /// </summary>
    [Property(MaxTest = 100)]
    public void AgentMatches_WhenLabelsAreSupersetOfRequired(NonEmptyString[] extraLabels)
    {
        var requiredLabels = new[] { "kiro", "dotnet" };
        var agentLabels = requiredLabels
            .Concat(extraLabels.Select(l => l.Get))
            .ToList();

        var registry = new AgentRegistryService(new Moq.Mock<Serilog.ILogger>().Object);
        var dispatcher = new JobDispatcherService(registry, new Moq.Mock<Serilog.ILogger>().Object);

        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host1",
            AgentType = "kiro-dotnet",
            Labels = agentLabels
        }, "conn-1");

        var selected = dispatcher.SelectAgent(requiredLabels);
        selected.Should().NotBeNull();
        selected!.AgentId.Should().Be("agent-1");
    }

    /// <summary>
    /// Property 23 (continued): Agent does NOT match when it's missing a required label.
    /// **Validates: Requirements 19.3**
    /// </summary>
    [Property(MaxTest = 50)]
    public void AgentDoesNotMatch_WhenMissingRequiredLabel(NonEmptyString missingLabel)
    {
        var missing = missingLabel.Get.Replace(",", "").Trim();
        if (string.IsNullOrEmpty(missing)) return;
        // Ensure the missing label is not already in the agent's labels
        if (missing == "kiro") return;

        var requiredLabels = new[] { "kiro", missing };
        var agentLabels = new[] { "kiro" }; // Missing the second required label

        var registry = new AgentRegistryService(new Moq.Mock<Serilog.ILogger>().Object);
        var dispatcher = new JobDispatcherService(registry, new Moq.Mock<Serilog.ILogger>().Object);

        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host1",
            AgentType = "kiro-dotnet",
            Labels = agentLabels
        }, "conn-1");

        var selected = dispatcher.SelectAgent(requiredLabels);
        selected.Should().BeNull();
    }
}
