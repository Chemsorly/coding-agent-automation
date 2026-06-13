using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.E2ETests.PageObjects;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// E2E tests for pipeline job template visibility in the Manual Dispatch dropdown.
/// Validates that pre-seeded templates appear in the template-select dropdown on /agent-coding.
/// </summary>
[Trait("Category", "E2E")]
public sealed class TemplateCrudTests : E2ETestBase, IClassFixture<E2EFixture>
{
    public TemplateCrudTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Templates_PreSeededTemplate_AppearsInDropdown()
    {
        // Arrange: seed a pipeline job template in the config store
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-crud-test",
            Name = "CRUD Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Act: navigate to /agent-coding
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Assert: the template appears as an option in the Manual Dispatch dropdown
        // Note: <option> elements inside <select> are not "visible" in Playwright's sense,
        // so we check the DOM for the option's existence using WaitForFunction.
        await Page.WaitForFunctionAsync(
            @"() => {
                const select = document.querySelector('[data-testid=""template-select""]');
                if (!select) return false;
                return Array.from(select.options).some(o => o.text === 'CRUD Test Template');
            }",
            null,
            new() { Timeout = 10_000 });

        // Double-check: verify we can actually select it
        var select = Page.Locator("[data-testid='template-select']");
        await select.SelectOptionAsync(new SelectOptionValue { Label = "CRUD Test Template" });
    }
}
