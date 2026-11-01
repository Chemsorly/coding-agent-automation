using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests verifying that Secrets, Settings and MCP Servers saved through the project page UI
/// reach the <see cref="JobAssignmentMessage"/> the agent fetches — the gap that
/// <see cref="DbModeProjectSettingsTests"/> leaves: it seeds the store directly rather than
/// proving that the UI write-path reaches the store and then the assignment.
///
/// Issue #4139. Issue identifiers 9311–9318 are reserved for this class.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class ProjectSettingsUiToAssignmentTests : E2ETestBase
{
    private const string ProjectA = "aaaaaaaa-0000-0000-0000-00000000a001";
    private const string ProjectB = "aaaaaaaa-0000-0000-0000-00000000b001";
    private const string TemplateA = "template-a-ui-settings-e2e";
    private const string TemplateB = "template-b-ui-settings-e2e";

    public ProjectSettingsUiToAssignmentTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds two projects and the shared routing profile used across all tests that dispatch.
    /// A template is seeded in each project; both use the same issue-e2e/repo-e2e providers,
    /// which is fine when seeded through the store directly (no API binding-conflict check).
    /// </summary>
    private async Task SeedTwoProjectsAsync()
    {
        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = ProjectA,
            Name = "Project A UI Settings",
            Enabled = true,
            TemplateIds = new List<string>()
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
        {
            Id = ProjectB,
            Name = "Project B UI Settings",
            Enabled = true,
            TemplateIds = new List<string>()
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(ProjectA, new PipelineJobTemplate
        {
            Id = TemplateA,
            Name = "Template A",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(ProjectB, new PipelineJobTemplate
        {
            Id = TemplateB,
            Name = "Template B",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-ui-settings-e2e",
            DisplayName = "UI Settings Profile",
            JobTemplate = E2ERouting.Key("db-e2e"),
            Enabled = true
        });
    }

    /// <summary>
    /// Private copy of <c>HeadlessE2ETestBase.DispatchIssueAsync</c> — needed because this class
    /// extends <c>E2ETestBase</c> (browser-driving) which does not inherit the headless helpers.
    /// </summary>
    private async Task<DistributionResult> DispatchAsync(
        string issue, string projectId, CancellationToken ct = default)
    {
        var orchService = Fixture.ApiServices.GetRequiredService<IDispatchOrchestrationService>();
        var distributor = Fixture.ApiServices.GetRequiredService<IWorkDistributor>();

        var project = await Fixture.ConfigStore.GetProjectByIdAsync(projectId, ct)
            ?? throw new InvalidOperationException($"Project '{projectId}' not found in ConfigStore");

        var request = await orchService.PrepareDistributionRequestAsync(
            new ImplementationDispatchOrchestrationRequest
            {
                IssueIdentifier = issue,
                IssueProviderId = "issue-e2e",
                RepoProviderId = "repo-e2e",
                BrainProviderId = null,
                PipelineProviderId = null,
                InitiatedBy = "e2e-test",
                Project = project
            }, ct);

        if (request is null)
            return new DistributionResult(
                Success: false,
                WorkItemId: null,
                ErrorMessage: $"Orchestration failed for issue '{issue}' — issue not found, no matching profile, or dedup guard rejected.");

        return await distributor.DistributeAsync(request, ct);
    }

    /// <summary>
    /// Seeds the issue, dispatches it, waits for the agent assignment, completes the job,
    /// and polls the database until the work item is Succeeded before returning the assignment.
    /// Resets the agent's TCS so the caller can dispatch again immediately.
    ///
    /// IMPORTANT: call <c>ResetJobAssigned</c> only after <c>Succeeded</c> — see
    /// <c>DbModeProjectSettingsTests.cs:146-153</c> for why.
    /// </summary>
    private async Task<JobAssignmentMessage> AssignmentForAsync(
        FakeAgentClient agent, string issue, string projectId)
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issue,
            Title = $"UI Settings test — issue {issue}",
            Description = "## Requirements\nUI settings E2E\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        var result = await DispatchAsync(issue, projectId);
        Assert.True(result.Success, $"Dispatch for issue {issue} failed: {result.ErrorMessage}");

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await agent.AcceptAndCompleteJobAsync(assignment.JobId);

        var workItemId = Guid.Parse(result.WorkItemId!);
        await WaitUntilAsync(async () =>
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var item = await db.WorkItems.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == workItemId);
            return item?.Status == WorkItemStatus.Succeeded;
        }, timeout: TimeSpan.FromSeconds(15));

        agent.ResetJobAssigned();
        return assignment;
    }

    // ── Test 1 ────────────────────────────────────────────────────────────

    /// <summary>
    /// A secret entered in the project page Secrets tab reaches the assignment for that project
    /// and does not appear in the assignment for a different project.
    /// </summary>
    [Fact]
    public async Task SecretEnteredInUi_ReachesAssignment_OtherProjectHasNone()
    {
        await SeedTwoProjectsAsync();

        // Act: navigate to ProjectA, Secrets tab, add UI_KEY=ui-value
        var projectPage = new ProjectDetailPage(Page, BaseUrl);
        await projectPage.NavigateAsync(ProjectA);
        await projectPage.SelectTabAsync("Secrets");
        await Page.WaitForSelectorAsync("button:has-text('Save Secrets')", new() { Timeout = 10_000 });

        await Page.ClickAsync(".btn-add-inline");
        await Page.FillAsync(".secret-key-input", "UI_KEY");
        await Page.PressAsync(".secret-key-input", "Tab");
        await Page.FillAsync(".secret-value-input", "ui-value");
        await Page.PressAsync(".secret-value-input", "Tab");
        await Page.ClickAsync("button:has-text('Save Secrets')");
        await Page.WaitForSelectorAsync(
            ".inline-status-success:has-text('Project secrets saved.')",
            new() { Timeout = 10_000 });

        // Assert store
        var stored = await Fixture.ConfigStore.GetProjectByIdAsync(ProjectA, CancellationToken.None);
        Assert.NotNull(stored?.Secrets);
        Assert.Equal("ui-value", stored!.Secrets!["UI_KEY"]);

        // Connect agent after browser steps, before first dispatch
        await using var agent = new FakeAgentClient("ui-settings-agent", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Assert: ProjectA assignment carries the secret
        var assignmentA = await AssignmentForAsync(agent, "9311", ProjectA);
        Assert.NotNull(assignmentA.ProjectSecrets);
        Assert.Equal("ui-value", assignmentA.ProjectSecrets!["UI_KEY"]);

        // Assert: ProjectB assignment does not carry UI_KEY
        var assignmentB = await AssignmentForAsync(agent, "9312", ProjectB);
        Assert.True(
            assignmentB.ProjectSecrets is null || !assignmentB.ProjectSecrets.ContainsKey("UI_KEY"),
            "ProjectB assignment must not contain ProjectA's secret UI_KEY");
    }

    // ── Test 2 ────────────────────────────────────────────────────────────

    /// <summary>
    /// After a page reload, each stored secret value renders as ****. Saving with ****
    /// preserves the stored value (UiSecretMask.Restore path).
    /// </summary>
    [Fact]
    public async Task SecretAfterReload_IsMasked_AndSavingKeepsTheValue()
    {
        await SeedTwoProjectsAsync();

        // Arrange: seed ProjectA with a known secret directly in the store
        var projectA = await Fixture.ConfigStore.GetProjectByIdAsync(ProjectA, CancellationToken.None)
            ?? throw new InvalidOperationException("ProjectA not found");
        await Fixture.ConfigStore.SaveProjectAsync(
            projectA with { Secrets = new Dictionary<string, string> { ["UI_KEY"] = "ui-value" } },
            CancellationToken.None);

        // Act: open Secrets tab
        var projectPage = new ProjectDetailPage(Page, BaseUrl);
        await projectPage.NavigateAsync(ProjectA);
        await projectPage.SelectTabAsync("Secrets");
        await Page.WaitForSelectorAsync("button:has-text('Save Secrets')", new() { Timeout = 10_000 });

        // Assert: key is visible, value is masked
        Assert.Equal("UI_KEY", await Page.Locator(".secret-key-input").InputValueAsync());
        Assert.Equal("****", await Page.Locator(".secret-value-input").InputValueAsync());

        // Assert: page HTML does not reveal the plaintext secret
        var pageContent = await Page.ContentAsync();
        Assert.DoesNotContain("ui-value", pageContent, StringComparison.Ordinal);

        // Act: save with **** value
        await Page.ClickAsync("button:has-text('Save Secrets')");
        await Page.WaitForSelectorAsync(
            ".inline-status-success:has-text('Project secrets saved.')",
            new() { Timeout = 10_000 });

        // Assert: store still has the original plaintext value
        var updated = await Fixture.ConfigStore.GetProjectByIdAsync(ProjectA, CancellationToken.None);
        Assert.Equal("ui-value", updated?.Secrets?["UI_KEY"]);
    }

    // ── Test 3 ────────────────────────────────────────────────────────────

    /// <summary>
    /// A MaxRetries override set in the Settings tab reaches the assignment for the project,
    /// leaves the other project at the global default, and disappears from the store after
    /// reverting through the UI.
    /// </summary>
    [Fact]
    public async Task OverrideSetInUi_ReachesAssignment_ResetReturnsToGlobal()
    {
        await SeedTwoProjectsAsync();

        // Arrange: seed ProjectA with KEEP secret so we can verify other settings survive
        var projectA = await Fixture.ConfigStore.GetProjectByIdAsync(ProjectA, CancellationToken.None)
            ?? throw new InvalidOperationException("ProjectA not found");
        await Fixture.ConfigStore.SaveProjectAsync(
            projectA with { Secrets = new Dictionary<string, string> { ["KEEP"] = "kept" } },
            CancellationToken.None);

        // Act: override MaxRetries to 5 in Settings tab
        var projectPage = new ProjectDetailPage(Page, BaseUrl);
        await projectPage.NavigateAsync(ProjectA);
        await projectPage.SelectTabAsync("Settings");
        await Page.WaitForSelectorAsync("button:has-text('Save Settings')", new() { Timeout = 10_000 });

        var maxRetriesGroup = Page.Locator("[data-setting='MaxRetries']");
        await maxRetriesGroup.Locator("button.btn-override").ClickAsync();
        var numberInput = maxRetriesGroup.Locator("input[type='number']");
        await numberInput.WaitForAsync(new() { Timeout = 5_000 });
        await numberInput.FillAsync("5");
        await numberInput.PressAsync("Tab");
        await Page.ClickAsync("button:has-text('Save Settings')");
        await Page.WaitForSelectorAsync(
            ".inline-status-success:has-text('Project settings saved.')",
            new() { Timeout = 10_000 });

        // Assert store: MaxRetries==5, KEEP secret preserved
        var storedA = await Fixture.ConfigStore.GetProjectByIdAsync(ProjectA, CancellationToken.None);
        Assert.Equal(5, storedA?.MaxRetries);
        Assert.Equal("kept", storedA?.Secrets?["KEEP"]);

        // Connect agent and dispatch
        await using var agent = new FakeAgentClient("ui-settings-agent", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var assignmentA1 = await AssignmentForAsync(agent, "9313", ProjectA);
        Assert.Equal(5, assignmentA1.PipelineConfiguration.MaxRetries);

        var assignmentB = await AssignmentForAsync(agent, "9314", ProjectB);
        Assert.Equal(3, assignmentB.PipelineConfiguration.MaxRetries); // global default

        // Act: reload and revert the override
        await projectPage.NavigateAsync(ProjectA);
        await projectPage.SelectTabAsync("Settings");
        await Page.WaitForSelectorAsync("button:has-text('Save Settings')", new() { Timeout = 10_000 });

        var maxRetriesGroup2 = Page.Locator("[data-setting='MaxRetries']");
        await maxRetriesGroup2.Locator("button.btn-revert").ClickAsync();
        var hint = maxRetriesGroup2.Locator(".global-default-hint");
        await hint.WaitForAsync(new() { Timeout = 5_000 });
        var hintText = await hint.TextContentAsync();
        Assert.Contains("Using global default: 3", hintText ?? "", StringComparison.Ordinal);

        await Page.ClickAsync("button:has-text('Save Settings')");
        await Page.WaitForSelectorAsync(
            ".inline-status-success:has-text('Project settings saved.')",
            new() { Timeout = 10_000 });

        // Assert store: MaxRetries is now null (reverted to global)
        var revertedA = await Fixture.ConfigStore.GetProjectByIdAsync(ProjectA, CancellationToken.None);
        Assert.Null(revertedA?.MaxRetries);

        var assignmentA2 = await AssignmentForAsync(agent, "9315", ProjectA);
        Assert.Equal(3, assignmentA2.PipelineConfiguration.MaxRetries);
    }

    // ── Test 4 ────────────────────────────────────────────────────────────

    /// <summary>
    /// A nested override (CodeReview.MaxIterations) set through the Settings tab
    /// reaches the PipelineConfiguration in the assignment.
    /// </summary>
    [Fact]
    public async Task NestedOverrideSetInUi_ReachesAssignment()
    {
        await SeedTwoProjectsAsync();

        var projectPage = new ProjectDetailPage(Page, BaseUrl);
        await projectPage.NavigateAsync(ProjectA);
        await projectPage.SelectTabAsync("Settings");
        await Page.WaitForSelectorAsync("button:has-text('Save Settings')", new() { Timeout = 10_000 });

        var maxIterGroup = Page.Locator("[data-setting='CodeReview.MaxIterations']");
        await maxIterGroup.Locator("button.btn-override").ClickAsync();
        var numberInput = maxIterGroup.Locator("input[type='number']");
        await numberInput.WaitForAsync(new() { Timeout = 5_000 });
        await numberInput.FillAsync("2");
        await numberInput.PressAsync("Tab");
        await Page.ClickAsync("button:has-text('Save Settings')");
        await Page.WaitForSelectorAsync(
            ".inline-status-success:has-text('Project settings saved.')",
            new() { Timeout = 10_000 });

        // Assert store
        var stored = await Fixture.ConfigStore.GetProjectByIdAsync(ProjectA, CancellationToken.None);
        Assert.Equal(2, stored?.CodeReview?.MaxIterations);

        // Assert assignment
        await using var agent = new FakeAgentClient("ui-settings-agent", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var assignment = await AssignmentForAsync(agent, "9316", ProjectA);
        Assert.Equal(2, assignment.PipelineConfiguration.CodeReview.MaxIterations);
    }

    // ── Test 5 ────────────────────────────────────────────────────────────

    /// <summary>
    /// MCP servers added through the project page MCP Servers tab reach the assignment
    /// for that project and do not appear in the assignment for a different project.
    /// </summary>
    [Fact]
    public async Task McpServersAddedInUi_ReachAssignment_OtherProjectHasNone()
    {
        await SeedTwoProjectsAsync();

        var projectPage = new ProjectDetailPage(Page, BaseUrl);
        await projectPage.NavigateAsync(ProjectA);
        await projectPage.SelectTabAsync("MCP Servers");
        await Page.WaitForSelectorAsync("button:has-text('Save MCP Servers')", new() { Timeout = 10_000 });

        // Add ui-http: type=http, URL, Headers
        await Page.ClickAsync("button.btn-add");
        await Page.WaitForSelectorAsync(".provider-form", new() { Timeout = 5_000 });
        var httpForm = Page.Locator(".provider-form");
        await httpForm.Locator("input[type='text']").Nth(0).FillAsync("ui-http"); // Name
        await httpForm.Locator("select").SelectOptionAsync("http"); // Type
        // Wait for the URL field to appear — Blazor Server re-renders over SignalR after the
        // type change, so poll until the second text input exists rather than sleeping.
        await httpForm.Locator("input[type='text']").Nth(1).WaitForAsync(new() { Timeout = 5_000 });
        await httpForm.Locator("input[type='text']").Nth(1).FillAsync("https://mcp.example.test/mcp"); // URL
        await httpForm.Locator("textarea").FillAsync("Authorization=Bearer ui-token"); // Headers
        await httpForm.Locator("button.btn-save").ClickAsync();
        // Wait for form to close (+ Add MCP Server button reappears)
        var addBtn = Page.Locator("button.btn-add");
        await addBtn.WaitForAsync(new() { Timeout = 5_000 });

        // Add ui-stdio: type=stdio (default), Command, Args, EnvVars, uncheck Enabled
        await Page.ClickAsync("button.btn-add");
        await Page.WaitForSelectorAsync(".provider-form", new() { Timeout = 5_000 });
        var stdioForm = Page.Locator(".provider-form");
        await stdioForm.Locator("input[type='text']").Nth(0).FillAsync("ui-stdio"); // Name
        // Type stays as stdio (default). Poll for the Command field to be ready before
        // filling — the form renders asynchronously after the + Add click.
        await stdioForm.Locator("input[type='text']").Nth(1).WaitForAsync(new() { Timeout = 5_000 });
        await stdioForm.Locator("input[type='text']").Nth(1).FillAsync("uvx"); // Command
        await stdioForm.Locator("input[type='text']").Nth(2).FillAsync("some-mcp@1 --flag"); // Args
        await stdioForm.Locator("textarea").FillAsync("LOG=debug"); // Environment Variables
        await stdioForm.Locator("input[type='checkbox']").UncheckAsync(); // Uncheck Enabled
        await stdioForm.Locator("button.btn-save").ClickAsync();
        await addBtn.WaitForAsync(new() { Timeout = 5_000 });

        // Save MCP Servers
        await Page.ClickAsync("button:has-text('Save MCP Servers')");
        await Page.WaitForSelectorAsync(
            ".inline-status-success:has-text('Project MCP servers saved.')",
            new() { Timeout = 10_000 });

        // Connect agent and get assignments
        await using var agent = new FakeAgentClient("ui-settings-agent", "db-e2e");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var assignmentA = await AssignmentForAsync(agent, "9317", ProjectA);

        var uiHttp = assignmentA.McpServers.FirstOrDefault(s => s.Name == "ui-http");
        Assert.NotNull(uiHttp);
        Assert.Equal("https://mcp.example.test/mcp", uiHttp!.Url);
        Assert.Equal("Bearer ui-token", uiHttp.Headers["Authorization"]);
        Assert.False(uiHttp.Disabled);

        var uiStdio = assignmentA.McpServers.FirstOrDefault(s => s.Name == "ui-stdio");
        Assert.NotNull(uiStdio);
        Assert.Equal("uvx", uiStdio!.Command);
        Assert.Equal(new[] { "some-mcp@1", "--flag" }, uiStdio.Args);
        Assert.Equal("debug", uiStdio.Env["LOG"]);
        Assert.True(uiStdio.Disabled);

        var assignmentB = await AssignmentForAsync(agent, "9318", ProjectB);
        Assert.True(
            assignmentB.McpServers is null ||
            (!assignmentB.McpServers.Any(s => s.Name == "ui-http") &&
             !assignmentB.McpServers.Any(s => s.Name == "ui-stdio")),
            "ProjectB assignment must not contain ProjectA's MCP servers");
    }

    // ── Test 6 ────────────────────────────────────────────────────────────

    /// <summary>
    /// After a page reload, MCP HTTP server header values render as ****. Saving with ****
    /// preserves the stored header value (UiSecretMask.Restore path for MCP headers).
    /// </summary>
    [Fact]
    public async Task McpHeaderAfterReload_IsMasked_AndSavingKeepsTheValue()
    {
        await SeedTwoProjectsAsync();

        // Arrange: seed ProjectA with an HTTP MCP server whose header contains a secret
        var projectA = await Fixture.ConfigStore.GetProjectByIdAsync(ProjectA, CancellationToken.None)
            ?? throw new InvalidOperationException("ProjectA not found");
        await Fixture.ConfigStore.SaveProjectAsync(
            projectA with
            {
                McpServers = new[]
                {
                    new McpServerConfig
                    {
                        Name = "masked-mcp",
                        Type = "http",
                        Url = "https://mcp.example.test/masked",
                        Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer ui-token" }
                    }
                }
            },
            CancellationToken.None);

        // Act: open MCP Servers tab
        var projectPage = new ProjectDetailPage(Page, BaseUrl);
        await projectPage.NavigateAsync(ProjectA);
        await projectPage.SelectTabAsync("MCP Servers");
        await Page.WaitForSelectorAsync("button:has-text('Save MCP Servers')", new() { Timeout = 10_000 });

        // Click the Edit button for the server
        await Page.ClickAsync("button.btn-edit");
        await Page.WaitForSelectorAsync(".provider-form", new() { Timeout = 5_000 });

        // Assert: header value is masked — key must be preserved, only the value becomes ****
        var headersTextarea = Page.Locator(".provider-form textarea");
        var headersValue = await headersTextarea.InputValueAsync();
        Assert.StartsWith("Authorization=****", headersValue, StringComparison.Ordinal);
        Assert.DoesNotContain("ui-token", headersValue, StringComparison.Ordinal);

        // Act: click Save Server (with masked value), then Save MCP Servers
        await Page.ClickAsync(".provider-form button.btn-save");
        var addBtn = Page.Locator("button.btn-add");
        await addBtn.WaitForAsync(new() { Timeout = 5_000 });

        await Page.ClickAsync("button:has-text('Save MCP Servers')");
        await Page.WaitForSelectorAsync(
            ".inline-status-success:has-text('Project MCP servers saved.')",
            new() { Timeout = 10_000 });

        // Assert: store still has the original value
        var updated = await Fixture.ConfigStore.GetProjectByIdAsync(ProjectA, CancellationToken.None);
        var savedServer = updated?.McpServers?.FirstOrDefault(s => s.Name == "masked-mcp");
        Assert.NotNull(savedServer);
        Assert.Equal("Bearer ui-token", savedServer!.Headers["Authorization"]);
    }
}
