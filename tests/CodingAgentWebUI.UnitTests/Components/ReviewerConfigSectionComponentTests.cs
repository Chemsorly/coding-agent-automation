using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the ReviewerConfigSection component.
/// Covers rendering of reviewer configurations, add/edit/delete flows, and form validation.
/// </summary>
public class ReviewerConfigSectionComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;

    public ReviewerConfigSectionComponentTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>());
    }

    // ═══ Empty State ═══

    [Fact]
    public void Section_RendersHeader()
    {
        var cut = Render<ReviewerConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains("Reviewer Configurations", cut.Markup);
    }

    [Fact]
    public void Section_WhenNoConfigs_ShowsEmptyMessage()
    {
        var cut = Render<ReviewerConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains("No reviewer configurations", cut.Markup);
    }

    [Fact]
    public void Section_ShowsAddButton()
    {
        var cut = Render<ReviewerConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains("Add Reviewer Configuration", cut.Markup);
    }

    // ═══ Table Rendering ═══

    [Fact]
    public void Section_WithConfigs_ShowsTable()
    {
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new()
                {
                    Id = "rc-1",
                    DisplayName = "DotNet Reviewers",
                    MatchLabels = new[] { "dotnet", "csharp" },
                    Agents = new[] { new ReviewAgent { Name = "Correctness", Prompt = "Review for correctness" } },
                    Enabled = true,
                    ExecutionOrder = 1
                }
            });

        var cut = Render<ReviewerConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains("DotNet Reviewers", cut.Markup);
        Assert.Contains("dotnet", cut.Markup);
        Assert.Contains("csharp", cut.Markup);
        Assert.Contains("Correctness", cut.Markup);
    }

    [Fact]
    public void Section_WithGlobalConfig_ShowsGlobalBadge()
    {
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new()
                {
                    Id = "rc-global",
                    DisplayName = "Global Reviewer",
                    MatchLabels = Array.Empty<string>(),
                    Agents = new[] { new ReviewAgent { Name = "General", Prompt = "General review" } },
                    Enabled = true
                }
            });

        var cut = Render<ReviewerConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains("GLOBAL", cut.Markup);
        Assert.Contains("(all jobs)", cut.Markup);
    }

    [Fact]
    public void Section_ShowsEnabledStatus()
    {
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new()
                {
                    Id = "rc-1",
                    DisplayName = "Enabled Config",
                    MatchLabels = Array.Empty<string>(),
                    Agents = new[] { new ReviewAgent { Name = "Agent1", Prompt = "Prompt" } },
                    Enabled = true
                },
                new()
                {
                    Id = "rc-2",
                    DisplayName = "Disabled Config",
                    MatchLabels = Array.Empty<string>(),
                    Agents = new[] { new ReviewAgent { Name = "Agent2", Prompt = "Prompt" } },
                    Enabled = false
                }
            });

        var cut = Render<ReviewerConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains("✅", cut.Markup);
        Assert.Contains("❌", cut.Markup);
    }

    [Fact]
    public void Section_ShowsEditAndDeleteButtons()
    {
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new()
                {
                    Id = "rc-1",
                    DisplayName = "Test Config",
                    MatchLabels = Array.Empty<string>(),
                    Agents = new[] { new ReviewAgent { Name = "Agent", Prompt = "Prompt" } }
                }
            });

        var cut = Render<ReviewerConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains("Edit", cut.Markup);
        Assert.Contains("Delete", cut.Markup);
    }

    // ═══ Add Form ═══

    [Fact]
    public async Task Section_ClickAdd_ShowsForm()
    {
        var cut = Render<ReviewerConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Reviewer"));
        await addBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Create", cut.Markup);
        Assert.Contains("Display Name", cut.Markup);
        Assert.Contains("Match Labels", cut.Markup);
        Assert.Contains("Agent Name", cut.Markup);
        Assert.Contains("Agent Prompt", cut.Markup);
    }

    [Fact]
    public async Task Section_FormSave_WithEmptyName_ShowsError()
    {
        var cut = Render<ReviewerConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Reviewer"));
        await addBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Display Name is required", cut.Markup);
    }

    [Fact]
    public async Task Section_FormCancel_HidesForm()
    {
        var cut = Render<ReviewerConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Reviewer"));
        await addBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Display Name", cut.Markup);

        var cancelBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Cancel"));
        await cancelBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.DoesNotContain("Display Name", cut.Markup);
    }

    // ═══ Save Flow ═══

    [Fact]
    public async Task Section_FormSave_CallsConfigStore()
    {
        _mockStore.Setup(s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = Render<ReviewerConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Reviewer"));
        await addBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Fill in the form
        var inputs = cut.FindAll("input[type='text']");
        await inputs[0].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "My Reviewer" }); // DisplayName

        // Fill agent name and prompt
        var allTextInputs = cut.FindAll("input[type='text']");
        await allTextInputs[2].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Agent1" }); // Agent Name

        var textareas = cut.FindAll("textarea");
        await textareas[0].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Review prompt" }); // Agent Prompt

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Section_FormSave_InvokesOnShowStatus()
    {
        _mockStore.Setup(s => s.SaveReviewerConfigAsync(It.IsAny<ReviewerConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        (string Message, bool IsError) status = default;
        var cut = Render<ReviewerConfigSection>(p => p
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Reviewer"));
        await addBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Fill form
        var inputs = cut.FindAll("input[type='text']");
        await inputs[0].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "My Reviewer" });
        var allTextInputs = cut.FindAll("input[type='text']");
        await allTextInputs[2].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Agent1" });
        var textareas = cut.FindAll("textarea");
        await textareas[0].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Review prompt" });

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("saved", status.Message);
        Assert.False(status.IsError);
    }

    // ═══ Delete Flow ═══

    [Fact]
    public async Task Section_ClickDelete_ShowsConfirmation()
    {
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new()
                {
                    Id = "rc-1",
                    DisplayName = "To Delete",
                    MatchLabels = Array.Empty<string>(),
                    Agents = new[] { new ReviewAgent { Name = "Agent", Prompt = "Prompt" } }
                }
            });

        var cut = Render<ReviewerConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var deleteBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Delete"));
        await deleteBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Delete reviewer configuration", cut.Markup);
        Assert.Contains("To Delete", cut.Markup);
    }

    [Fact]
    public async Task Section_ConfirmDelete_CallsConfigStore()
    {
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReviewerConfiguration>
            {
                new()
                {
                    Id = "rc-1",
                    DisplayName = "To Delete",
                    MatchLabels = Array.Empty<string>(),
                    Agents = new[] { new ReviewAgent { Name = "Agent", Prompt = "Prompt" } }
                }
            });
        _mockStore.Setup(s => s.DeleteReviewerConfigAsync("rc-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = Render<ReviewerConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        // Click Delete to show confirmation
        var deleteBtn = cut.FindAll("button").First(b => b.TextContent.Trim() == "Delete");
        await deleteBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Click Confirm Delete
        var confirmBtn = cut.FindAll("button.btn-delete").Last();
        await confirmBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.DeleteReviewerConfigAsync("rc-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══ Error Handling ═══

    [Fact]
    public async Task Section_LoadFails_InvokesOnShowStatus()
    {
        _mockStore.Setup(s => s.LoadReviewerConfigsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("disk error"));

        (string Message, bool IsError) status = default;
        var cut = Render<ReviewerConfigSection>(p => p
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        Assert.Contains("Failed to load", status.Message);
        Assert.True(status.IsError);
    }
}
