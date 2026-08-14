using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for Housekeeping configuration fields and effective limit resolution
/// (spec 040, task 2.3).
/// </summary>
public class HousekeepingConfigTests
{
    // ── PipelineConfiguration defaults ──────────────────────────────────

    [Fact]
    public void PipelineConfiguration_HousekeepingConcurrencyLimit_DefaultIsOne()
    {
        var config = new PipelineConfiguration();
        Assert.Equal(1, config.HousekeepingConcurrencyLimit);
    }

    // ── PipelineJobTemplate defaults ─────────────────────────────────────

    [Fact]
    public void PipelineJobTemplate_HousekeepingEnabled_DefaultIsFalse()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            Name = "Test"
        };
        Assert.False(template.HousekeepingEnabled);
    }

    [Fact]
    public void PipelineJobTemplate_HousekeepingConcurrencyLimit_DefaultIsNull()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            Name = "Test"
        };
        Assert.Null(template.HousekeepingConcurrencyLimit);
    }

    // ── Effective limit resolution ────────────────────────────────────────

    [Fact]
    public void EffectiveLimit_WhenTemplateOverrideIsNull_UsesGlobal()
    {
        var config = new PipelineConfiguration { HousekeepingConcurrencyLimit = 3 };
        var template = new PipelineJobTemplate
        {
            Id = "t1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            Name = "Test",
            HousekeepingConcurrencyLimit = null
        };

        var effective = Math.Max(1, template.HousekeepingConcurrencyLimit ?? config.HousekeepingConcurrencyLimit);
        Assert.Equal(3, effective);
    }

    [Fact]
    public void EffectiveLimit_WhenTemplateOverrideIsSet_UsesTemplateValue()
    {
        var config = new PipelineConfiguration { HousekeepingConcurrencyLimit = 1 };
        var template = new PipelineJobTemplate
        {
            Id = "t1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            Name = "Test",
            HousekeepingConcurrencyLimit = 3
        };

        var effective = Math.Max(1, template.HousekeepingConcurrencyLimit ?? config.HousekeepingConcurrencyLimit);
        Assert.Equal(3, effective);
    }

    [Fact]
    public void EffectiveLimit_WhenGlobalIsZero_ClampsToOne()
    {
        var config = new PipelineConfiguration { HousekeepingConcurrencyLimit = 0 };
        var template = new PipelineJobTemplate
        {
            Id = "t1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            Name = "Test",
            HousekeepingConcurrencyLimit = null
        };

        var effective = Math.Max(1, template.HousekeepingConcurrencyLimit ?? config.HousekeepingConcurrencyLimit);
        Assert.Equal(1, effective);
    }

    [Fact]
    public void EffectiveLimit_WhenTemplateOverrideIsZero_ClampsToOne()
    {
        var config = new PipelineConfiguration { HousekeepingConcurrencyLimit = 5 };
        var template = new PipelineJobTemplate
        {
            Id = "t1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            Name = "Test",
            HousekeepingConcurrencyLimit = 0
        };

        var effective = Math.Max(1, template.HousekeepingConcurrencyLimit ?? config.HousekeepingConcurrencyLimit);
        Assert.Equal(1, effective);
    }
}
