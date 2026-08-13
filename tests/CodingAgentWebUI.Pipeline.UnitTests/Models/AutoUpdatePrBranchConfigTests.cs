using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for AutoUpdatePrBranch configuration fields and effective limit resolution
/// (spec 040, task 2.3).
/// </summary>
public class AutoUpdatePrBranchConfigTests
{
    // ── PipelineConfiguration defaults ──────────────────────────────────

    [Fact]
    public void PipelineConfiguration_AutoUpdatePrBranchConcurrencyLimit_DefaultIsOne()
    {
        var config = new PipelineConfiguration();
        Assert.Equal(1, config.AutoUpdatePrBranchConcurrencyLimit);
    }

    // ── PipelineJobTemplate defaults ─────────────────────────────────────

    [Fact]
    public void PipelineJobTemplate_AutoUpdatePrBranches_DefaultIsFalse()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            Name = "Test"
        };
        Assert.False(template.AutoUpdatePrBranches);
    }

    [Fact]
    public void PipelineJobTemplate_AutoUpdatePrBranchConcurrencyLimit_DefaultIsNull()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            Name = "Test"
        };
        Assert.Null(template.AutoUpdatePrBranchConcurrencyLimit);
    }

    // ── Effective limit resolution ────────────────────────────────────────

    [Fact]
    public void EffectiveLimit_WhenTemplateOverrideIsNull_UsesGlobal()
    {
        var config = new PipelineConfiguration { AutoUpdatePrBranchConcurrencyLimit = 3 };
        var template = new PipelineJobTemplate
        {
            Id = "t1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            Name = "Test",
            AutoUpdatePrBranchConcurrencyLimit = null
        };

        var effective = Math.Max(1, template.AutoUpdatePrBranchConcurrencyLimit ?? config.AutoUpdatePrBranchConcurrencyLimit);
        Assert.Equal(3, effective);
    }

    [Fact]
    public void EffectiveLimit_WhenTemplateOverrideIsSet_UsesTemplateValue()
    {
        var config = new PipelineConfiguration { AutoUpdatePrBranchConcurrencyLimit = 1 };
        var template = new PipelineJobTemplate
        {
            Id = "t1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            Name = "Test",
            AutoUpdatePrBranchConcurrencyLimit = 3
        };

        var effective = Math.Max(1, template.AutoUpdatePrBranchConcurrencyLimit ?? config.AutoUpdatePrBranchConcurrencyLimit);
        Assert.Equal(3, effective);
    }

    [Fact]
    public void EffectiveLimit_WhenGlobalIsZero_ClampsToOne()
    {
        var config = new PipelineConfiguration { AutoUpdatePrBranchConcurrencyLimit = 0 };
        var template = new PipelineJobTemplate
        {
            Id = "t1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            Name = "Test",
            AutoUpdatePrBranchConcurrencyLimit = null
        };

        var effective = Math.Max(1, template.AutoUpdatePrBranchConcurrencyLimit ?? config.AutoUpdatePrBranchConcurrencyLimit);
        Assert.Equal(1, effective);
    }

    [Fact]
    public void EffectiveLimit_WhenTemplateOverrideIsZero_ClampsToOne()
    {
        var config = new PipelineConfiguration { AutoUpdatePrBranchConcurrencyLimit = 5 };
        var template = new PipelineJobTemplate
        {
            Id = "t1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            Name = "Test",
            AutoUpdatePrBranchConcurrencyLimit = 0
        };

        var effective = Math.Max(1, template.AutoUpdatePrBranchConcurrencyLimit ?? config.AutoUpdatePrBranchConcurrencyLimit);
        Assert.Equal(1, effective);
    }
}
