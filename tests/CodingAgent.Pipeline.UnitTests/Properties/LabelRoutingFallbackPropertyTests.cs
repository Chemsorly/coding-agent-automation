using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for label routing fallback resolution and agent matching.
/// </summary>
/// <remarks>
/// TODO: [WARNING] Two property tests that validated <c>LabelMatchHelper.IsLabelMatch</c>
/// were deleted in issue #2325 alongside <c>AgentReservationService</c>:
///   - AgentMatches_WhenLabelsAreSupersetOfRequired
///   - AgentDoesNotMatch_WhenMissingRequiredLabel
/// <c>LabelMatchHelper.IsLabelMatch</c> is still live code (used by <c>AgentRegistryService</c>
/// for profile resolution). Its case-sensitivity and subset-logic invariants are no longer
/// covered by property-based tests — only fixed-input scenario tests in
/// <c>LabelMappingIntegrationTests.cs</c> remain. Add replacement property tests directly
/// against <c>LabelMatchHelper.IsLabelMatch</c>.
/// Tracked by review findings for issue #2325.
/// </remarks>
public class LabelRoutingFallbackPropertyTests
{
    /// <summary>
    /// Property 23: Label Routing Fallback
    /// When repo config has RequiredLabels, those are used.
    /// **Validates: Requirements 19.3**
    /// </summary>
    [Property(MaxTest = 20)]
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
            RequiredLabels = new List<string> { l1, l2 }
        };

        var pipelineConfig = new PipelineConfiguration
        {
            DefaultRequiredAgentLabels = "fallback-label"
        };

        var resolved = LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);

        resolved.Should().Contain(l1);
        resolved.Should().Contain(l2);
        resolved.Should().NotContain("fallback-label");
    }

    /// <summary>
    /// Property 23 (continued): When repo config has no RequiredLabels,
    /// falls back to DefaultRequiredAgentLabels.
    /// **Validates: Requirements 19.3**
    /// </summary>
    [Property(MaxTest = 20)]
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

        var resolved = LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);

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

        var resolved = LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);

        resolved.Should().BeEmpty();
    }

}
