using Bunit;
using Moq;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the QualityGateConfigSection component.
/// Covers rendering of quality gate configurations, add/edit/delete flows, and form validation.
/// </summary>
public class QualityGateConfigSectionComponentTests : BunitContext
{
    private readonly Mock<IConfigurationStore> _mockStore;

    public QualityGateConfigSectionComponentTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>());
    }

    // ═══ Empty State ═══

    [Fact]
    public void Section_RendersHeader()
    {
        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains("Quality Gate Configurations", cut.Markup);
    }

    [Fact]
    public void Section_WhenNoConfigs_ShowsEmptyMessage()
    {
        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains("No quality gate configurations", cut.Markup);
    }

    [Fact]
    public void Section_ShowsAddButton()
    {
        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains("Add Quality Gate Configuration", cut.Markup);
    }

    // ═══ Table Rendering ═══

    [Fact]
    public void Section_WithConfigs_ShowsTable()
    {
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>
            {
                new()
                {
                    Id = "qgc-1",
                    DisplayName = ".NET Quality Gates",
                    MatchLabels = new[] { "dotnet" },
                    CompilationCommand = "dotnet",
                    CompilationArguments = new[] { "build", "--no-restore" },
                    TestCommand = "dotnet",
                    TestArguments = new[] { "test", "--no-restore" },
                    CoverageThreshold = 80.0,
                    Enabled = true,
                    ExecutionOrder = 1
                }
            });

        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains(".NET Quality Gates", cut.Markup);
        Assert.Contains("dotnet", cut.Markup);
        Assert.Contains("80", cut.Markup);
    }

    [Fact]
    public void Section_WithGlobalConfig_ShowsGlobalBadge()
    {
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>
            {
                new()
                {
                    Id = "qgc-global",
                    DisplayName = "Global QG",
                    MatchLabels = Array.Empty<string>(),
                    CompilationCommand = "make",
                    Enabled = true
                }
            });

        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains("GLOBAL", cut.Markup);
        Assert.Contains("(all jobs)", cut.Markup);
    }

    [Fact]
    public void Section_ShowsEnabledStatus()
    {
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>
            {
                new()
                {
                    Id = "qgc-1",
                    DisplayName = "Enabled",
                    CompilationCommand = "dotnet",
                    Enabled = true
                },
                new()
                {
                    Id = "qgc-2",
                    DisplayName = "Disabled",
                    CompilationCommand = "dotnet",
                    Enabled = false
                }
            });

        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        // TODO: Assertions don't verify icon-to-row association — test would pass even if conditional logic were inverted
        Assert.NotNull(cut.Find("[data-icon=\"check-circle\"]"));
        Assert.NotNull(cut.Find("[data-icon=\"x-circle\"]"));
    }

    [Fact]
    public void Section_ShowsNoCoverageThreshold_AsDash()
    {
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>
            {
                new()
                {
                    Id = "qgc-1",
                    DisplayName = "No Coverage",
                    CompilationCommand = "dotnet",
                    CoverageThreshold = null,
                    Enabled = true
                }
            });

        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        // The "—" dash is used when no coverage threshold
        Assert.Contains("—", cut.Markup);
    }

    [Fact]
    public void Section_ShowsEditAndDeleteButtons()
    {
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>
            {
                new()
                {
                    Id = "qgc-1",
                    DisplayName = "Test QGC",
                    CompilationCommand = "dotnet",
                    Enabled = true
                }
            });

        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains("Edit", cut.Markup);
        Assert.Contains("Delete", cut.Markup);
    }

    // ═══ Add Form ═══

    [Fact]
    public async Task Section_ClickAdd_ShowsForm()
    {
        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Quality Gate"));
        await addBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Create", cut.Markup);
        Assert.Contains("Display Name", cut.Markup);
        Assert.Contains("Compilation Command", cut.Markup);
        Assert.Contains("Test Command", cut.Markup);
        Assert.Contains("Coverage Threshold", cut.Markup);
    }

    [Fact]
    public async Task Section_FormSave_WithEmptyName_ShowsError()
    {
        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Quality Gate"));
        await addBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Display Name is required", cut.Markup);
    }

    [Fact]
    public async Task Section_FormSave_WithNoCommands_ShowsError()
    {
        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Quality Gate"));
        await addBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Fill display name but leave commands empty
        var inputs = cut.FindAll("input[type='text']");
        await inputs[0].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "My QGC" });

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("At least one of Compilation Command or Test Command must be specified", cut.Markup);
    }

    [Fact]
    public async Task Section_FormCancel_HidesForm()
    {
        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Quality Gate"));
        await addBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Display Name", cut.Markup);

        var cancelBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Cancel"));
        await cancelBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Form should be hidden — the "Create" heading should be gone
        Assert.DoesNotContain("Compilation Command", cut.Markup);
    }

    // ═══ Save Flow ═══

    [Fact]
    public async Task Section_FormSave_CallsConfigStore()
    {
        _mockStore.Setup(s => s.SaveQualityGateConfigAsync(It.IsAny<QualityGateConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Quality Gate"));
        await addBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Fill in the form — re-find elements after each change to avoid stale references
        var inputs = cut.FindAll("input[type='text']");
        await inputs[0].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "My QGC" }); // DisplayName

        inputs = cut.FindAll("input[type='text']");
        await inputs[2].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "dotnet" }); // CompilationCommand

        inputs = cut.FindAll("input[type='text']");
        await inputs[3].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "build" }); // CompilationArguments

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.SaveQualityGateConfigAsync(It.IsAny<QualityGateConfiguration>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Section_FormSave_InvokesOnShowStatus()
    {
        _mockStore.Setup(s => s.SaveQualityGateConfigAsync(It.IsAny<QualityGateConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        (string Message, bool IsError) status = default;
        var cut = Render<QualityGateConfigSection>(p => p
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Quality Gate"));
        await addBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var inputs = cut.FindAll("input[type='text']");
        await inputs[0].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "My QGC" });

        inputs = cut.FindAll("input[type='text']");
        await inputs[2].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "dotnet" });

        inputs = cut.FindAll("input[type='text']");
        await inputs[3].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "build" });

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("saved", status.Message);
        Assert.False(status.IsError);
    }

    // ═══ Delete Flow ═══

    [Fact]
    public async Task Section_ClickDelete_ShowsConfirmation()
    {
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>
            {
                new()
                {
                    Id = "qgc-1",
                    DisplayName = "To Delete",
                    CompilationCommand = "dotnet",
                    Enabled = true
                }
            });

        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var deleteBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Delete"));
        await deleteBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Delete quality gate configuration", cut.Markup);
        Assert.Contains("To Delete", cut.Markup);
    }

    [Fact]
    public async Task Section_ConfirmDelete_CallsConfigStore()
    {
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>
            {
                new()
                {
                    Id = "qgc-1",
                    DisplayName = "To Delete",
                    CompilationCommand = "dotnet",
                    Enabled = true
                }
            });
        _mockStore.Setup(s => s.DeleteQualityGateConfigAsync("qgc-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        // Click Delete to show confirmation
        var deleteBtn = cut.FindAll("button").First(b => b.TextContent.Trim() == "Delete");
        await deleteBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Click Confirm Delete
        var confirmBtn = cut.FindAll("button.btn-delete").Last();
        await confirmBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _mockStore.Verify(s => s.DeleteQualityGateConfigAsync("qgc-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══ Error Handling ═══

    [Fact]
    public async Task Section_LoadFails_InvokesOnShowStatus()
    {
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("disk error"));

        (string Message, bool IsError) status = default;
        var cut = Render<QualityGateConfigSection>(p => p
            .Add(s => s.ConfigStore, _mockStore.Object)
            .Add(s => s.OnShowStatus, EventCallback.Factory.Create<(string, bool)>(this, v => status = v)));

        Assert.Contains("Failed to load", status.Message);
        Assert.True(status.IsError);
    }

    [Fact]
    public async Task Section_SaveFails_ShowsFormError()
    {
        _mockStore.Setup(s => s.SaveQualityGateConfigAsync(It.IsAny<QualityGateConfiguration>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("write error"));

        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        var addBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Add Quality Gate"));
        await addBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var inputs = cut.FindAll("input[type='text']");
        await inputs[0].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "My QGC" });

        inputs = cut.FindAll("input[type='text']");
        await inputs[2].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "dotnet" });

        inputs = cut.FindAll("input[type='text']");
        await inputs[3].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "build" });

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("write error", cut.Markup);
    }

    // ═══ Table Headers ═══

    [Fact]
    public void Section_WithConfigs_ShowsTableHeaders()
    {
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration>
            {
                new()
                {
                    Id = "qgc-1",
                    DisplayName = "Test",
                    CompilationCommand = "dotnet",
                    Enabled = true
                }
            });

        var cut = Render<QualityGateConfigSection>(p => p.Add(s => s.ConfigStore, _mockStore.Object));

        Assert.Contains("Display Name", cut.Markup);
        Assert.Contains("Match Labels", cut.Markup);
        Assert.Contains("Commands", cut.Markup);
        Assert.Contains("Coverage", cut.Markup);
        Assert.Contains("Enabled", cut.Markup);
        Assert.Contains("Order", cut.Markup);
        Assert.Contains("Actions", cut.Markup);
    }
}
