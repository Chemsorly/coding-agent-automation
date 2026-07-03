using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// E2E tests for the Global Defaults settings sections.
/// Validates: navigate → select section → verify fields render → modify → save → reload → verify persisted.
/// </summary>
[Trait("Category", "E2E")]
public sealed class SettingsGlobalDefaultsTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public SettingsGlobalDefaultsTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task General_RendersAllFields_AndSavesSuccessfully()
    {
        var settingsPage = new SettingsPage(Page, BaseUrl);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("General");

        // Verify key fields render
        await Page.WaitForSelectorAsync("text=Max Retries", new() { Timeout = 5_000 });

        // Expand "Advanced settings" to reveal hidden fields
        await settingsPage.ExpandAdvancedSectionsAsync();

        var markup = await Page.ContentAsync();
        Assert.Contains("Max Retries", markup);
        Assert.Contains("Max Analysis Retries", markup);
        Assert.Contains("Agent Timeout", markup);
        Assert.Contains("Stall Warning Interval", markup);
        Assert.Contains("Baseline Health Check", markup);
        Assert.Contains("Workspace Base Directory", markup);
        Assert.Contains("Issue Page Size", markup);
        Assert.Contains("Blacklisted Paths", markup);

        // Click save
        var saveBtn = Page.Locator("button:has-text('Save General')");
        await saveBtn.ClickAsync();
        await Page.WaitForTimeoutAsync(1500);

        // Verify success status
        var status = await Page.Locator("div.inline-status, div.settings-status").First.TextContentAsync();
        Assert.Contains("saved", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PipelineLoop_RendersAllFields_AndSavesSuccessfully()
    {
        var settingsPage = new SettingsPage(Page, BaseUrl);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Pipeline Loop");

        await Page.WaitForSelectorAsync("text=Poll Interval", new() { Timeout = 5_000 });
        await settingsPage.ExpandAdvancedSectionsAsync();
        var markup = await Page.ContentAsync();
        Assert.Contains("Poll Interval", markup);
        Assert.Contains("Max Runs Per Cycle", markup);
        Assert.Contains("Max Pages to Fetch", markup);
        Assert.Contains("Max Consecutive Poll Failures", markup);
        Assert.Contains("Max Backoff Interval", markup);
        Assert.Contains("Circuit Breaker Cooldown", markup);

        var saveBtn = Page.Locator("button:has-text('Save Pipeline Loop')");
        await saveBtn.ClickAsync();
        await Page.WaitForTimeoutAsync(1500);

        var status = await Page.Locator("div.inline-status, div.settings-status").First.TextContentAsync();
        Assert.Contains("saved", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Prompts_RendersAllFields_AndSavesSuccessfully()
    {
        var settingsPage = new SettingsPage(Page, BaseUrl);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Prompts");

        await Page.WaitForSelectorAsync("text=Analysis Prompt", new() { Timeout = 5_000 });
        await settingsPage.ExpandAdvancedSectionsAsync();
        var markup = await Page.ContentAsync();
        Assert.Contains("Analysis Prompt", markup);
        Assert.Contains("Implementation Prompt", markup);
        Assert.Contains("Acceptance Criteria", markup);
        Assert.Contains("Analysis Review", markup);
        Assert.Contains("Analysis Refinement Prompt", markup);

        // Verify textareas rendered
        var textareas = await Page.Locator("textarea").CountAsync();
        Assert.Equal(5, textareas);

        var saveBtn = Page.Locator("button:has-text('Save Prompt')");
        await saveBtn.ClickAsync();
        await Page.WaitForTimeoutAsync(1500);

        var status = await Page.Locator("div.inline-status, div.settings-status").First.TextContentAsync();
        Assert.Contains("saved", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Decomposition_RendersAllFields_AndSavesSuccessfully()
    {
        var settingsPage = new SettingsPage(Page, BaseUrl);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Decomposition");

        await Page.WaitForSelectorAsync("text=Max Sub-Issues", new() { Timeout = 5_000 });
        await settingsPage.ExpandAdvancedSectionsAsync();
        var markup = await Page.ContentAsync();
        Assert.Contains("Max Sub-Issues Per Epic", markup);
        Assert.Contains("Max Concurrent Decompositions", markup);
        Assert.Contains("Decomposition Timeout", markup);
        Assert.Contains("Max Open Issues for Context", markup);

        var saveBtn = Page.Locator("button:has-text('Save Decomposition')");
        await saveBtn.ClickAsync();
        await Page.WaitForTimeoutAsync(1500);

        var status = await Page.Locator("div.inline-status, div.settings-status").First.TextContentAsync();
        Assert.Contains("saved", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Implementation_RendersAllFields_AndSavesSuccessfully()
    {
        var settingsPage = new SettingsPage(Page, BaseUrl);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Implementation");

        await Page.WaitForSelectorAsync("text=Agent Code Review", new() { Timeout = 5_000 });
        await settingsPage.ExpandAdvancedSectionsAsync();
        var markup = await Page.ContentAsync();
        Assert.Contains("Agent Code Review Enabled", markup);
        Assert.Contains("Max Review Iterations", markup);
        Assert.Contains("Fix Prompt", markup);

        var saveBtn = Page.Locator("button:has-text('Save Implementation')");
        await saveBtn.ClickAsync();
        await Page.WaitForTimeoutAsync(1500);

        var status = await Page.Locator("div.inline-status, div.settings-status").First.TextContentAsync();
        Assert.Contains("saved", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Review_RendersAllFields_AndSavesSuccessfully()
    {
        var settingsPage = new SettingsPage(Page, BaseUrl);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Review");

        await Page.WaitForSelectorAsync("text=Enable Inline Review Comments", new() { Timeout = 5_000 });
        await settingsPage.ExpandAdvancedSectionsAsync();
        var markup = await Page.ContentAsync();
        Assert.Contains("Enable Inline Review Comments", markup);
        Assert.Contains("Minimum Severity", markup);
        Assert.Contains("Maximum Inline Comments", markup);
        Assert.Contains("Prioritize by Severity", markup);
        Assert.Contains("Format Correction Retries", markup);

        var saveBtn = Page.Locator("button:has-text('Save Review')");
        await saveBtn.ClickAsync();
        await Page.WaitForTimeoutAsync(1500);

        var status = await Page.Locator("div.inline-status, div.settings-status").First.TextContentAsync();
        Assert.Contains("saved", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Consolidation_RendersAllFields_AndSavesSuccessfully()
    {
        var settingsPage = new SettingsPage(Page, BaseUrl);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Consolidation");

        await Page.WaitForSelectorAsync("text=Max Refactoring Proposals", new() { Timeout = 5_000 });
        await settingsPage.ExpandAdvancedSectionsAsync();
        var markup = await Page.ContentAsync();
        Assert.Contains("Max Refactoring Proposals", markup);
        Assert.Contains("Hotspot Analysis Lookback", markup);
        Assert.Contains("Refactoring Outcome Lookback", markup);
        Assert.Contains("Refactoring Review", markup);
        Assert.Contains("Brain Consolidation Review", markup);
        Assert.Contains("Harness Suggestions Review", markup);

        var saveBtn = Page.Locator("button:has-text('Save Consolidation')");
        await saveBtn.ClickAsync();
        await Page.WaitForTimeoutAsync(1500);

        var status = await Page.Locator("div.inline-status, div.settings-status").First.TextContentAsync();
        Assert.Contains("saved", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Advanced_RendersAllFields_AndSavesSuccessfully()
    {
        var settingsPage = new SettingsPage(Page, BaseUrl);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Advanced");

        await Page.WaitForSelectorAsync("text=Agent Routing", new() { Timeout = 5_000 });
        var markup = await Page.ContentAsync();
        Assert.Contains("Agent Routing", markup);
        Assert.Contains("Default Required Agent Labels", markup);
        Assert.Contains("Brain Repository", markup);
        Assert.Contains("Brain Push Max Retries", markup);
        Assert.Contains("Agent Health Monitoring", markup);
        Assert.Contains("Agent Disconnect Grace Period", markup);
        Assert.Contains("Heartbeat Sweep Interval", markup);
        Assert.Contains("Buffer Capacities", markup);
        Assert.Contains("Output Buffer Capacity", markup);

        var saveBtn = Page.Locator("button:has-text('Save Advanced')");
        await saveBtn.ClickAsync();
        await Page.WaitForTimeoutAsync(1500);

        var status = await Page.Locator("div.inline-status, div.settings-status").First.TextContentAsync();
        Assert.Contains("saved", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task General_ModifyAndSave_PersistsToStore()
    {
        var settingsPage = new SettingsPage(Page, BaseUrl);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("General");

        await Page.WaitForSelectorAsync("text=Max Retries", new() { Timeout = 5_000 });

        // Modify Max Retries field — find first number input
        var maxRetriesInput = Page.Locator("input[type='number']").First;
        await maxRetriesInput.FillAsync("7");

        // Save
        var saveBtn = Page.Locator("button:has-text('Save General')");
        await saveBtn.ClickAsync();
        await Page.WaitForTimeoutAsync(1500);

        // Verify persisted in store
        var configStore = Fixture.Factory.Services.GetRequiredService<IConfigurationStore>();
        var config = await configStore.LoadPipelineConfigAsync(CancellationToken.None);
        Assert.Equal(7, config.MaxRetries);
    }

    [Fact]
    public async Task Advanced_ModifyLabels_PersistsToStore()
    {
        var settingsPage = new SettingsPage(Page, BaseUrl);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Advanced");

        await Page.WaitForSelectorAsync("text=Default Required Agent Labels", new() { Timeout = 5_000 });

        // Fill in labels
        var labelsInput = Page.Locator("input[type='text']").First;
        await labelsInput.FillAsync("kiro,python");

        // Save
        var saveBtn = Page.Locator("button:has-text('Save Advanced')");
        await saveBtn.ClickAsync();
        await Page.WaitForTimeoutAsync(1500);

        // Verify persisted
        var configStore = Fixture.Factory.Services.GetRequiredService<IConfigurationStore>();
        var config = await configStore.LoadPipelineConfigAsync(CancellationToken.None);
        Assert.Equal("kiro,python", config.DefaultRequiredAgentLabels);
    }
}
