using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Helper methods for generating test data for multi-repo pipeline loop property tests.
/// Feature: 013-multi-repo-pipeline-loop
/// </summary>
public static class PipelineLoopTestData
{
    /// <summary>Creates a list of enabled templates with unique IssueProviderIds.</summary>
    public static List<PipelineJobTemplate> CreateTemplates(int count, bool allEnabled = true)
    {
        return Enumerable.Range(0, count).Select(i => new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"Template-{i}",
            IssueProviderId = $"ip-{i}",
            RepoProviderId = $"rp-{i}",
            BrainProviderId = i % 2 == 0 ? $"bp-{i}" : null,
            PipelineProviderId = i % 3 == 0 ? $"pp-{i}" : null,
            Enabled = allEnabled || i % 2 == 0
        }).ToList();
    }

    /// <summary>Creates a mixed list with some enabled and some disabled templates.</summary>
    public static List<PipelineJobTemplate> CreateMixedTemplates(int enabledCount, int disabledCount)
    {
        var enabled = Enumerable.Range(0, enabledCount).Select(i => new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"Enabled-{i}",
            IssueProviderId = $"ip-e-{i}",
            RepoProviderId = $"rp-e-{i}",
            Enabled = true
        }).ToList();

        var disabled = Enumerable.Range(0, disabledCount).Select(i => new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"Disabled-{i}",
            IssueProviderId = $"ip-d-{i}",
            RepoProviderId = $"rp-d-{i}",
            Enabled = false
        }).ToList();

        return enabled.Concat(disabled).ToList();
    }
}
