using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the repository page at /pipelines/{templateId}.
/// Verifies navigation from the Pipelines table, steering save, and reload.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RepositoryPageTests : E2ETestBase
{
    public RepositoryPageTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RepositoryPage_EditSteering_SaveAndReload()
    {
        // NOTE: This test does not wrap act/assert in a try/finally { await Fixture.ResetAllAsync(); }
        // block. If the test fails after seeding (e.g. navigation to the repository page times out),
        // the template with id "repo-page-test-id" remains in the config store and may interfere with
        // subsequent tests in this sequential collection. Add the try/finally pattern used by all other
        // E2E tests that mutate the config store. (TestQualityReviewer finding, issue #3576)
        // Arrange: seed a template
        const string templateId = "repo-page-test-id";
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = templateId,
            Name = "Steering Test Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            SteeringContent = null,
        }, CancellationToken.None);

        // Act: navigate to /agent-coding and click the template name link
        var codingPage = new AgentCodingPage(Page, BaseUrl);
        await codingPage.NavigateAsync();

        // Find and click the template name link in the Pipelines table
        var templateLink = Page.Locator($"a[href='/pipelines/{templateId}']").First;
        await templateLink.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await templateLink.ClickAsync();

        // Wait for navigation to the repository page
        await Page.WaitForURLAsync($"**/pipelines/{templateId}", new() { Timeout = 10_000 });
        await Page.WaitForSelectorAsync("h1", new() { Timeout = 10_000 });

        // Assert: we landed on the correct page
        var pageText = await Page.TextContentAsync("h1");
        Assert.Contains("Steering Test Template", pageText);

        // Act: change the steering content in the Behaviour card
        var repoPage = new RepositoryPageObject(Page, BaseUrl);
        const string newSteering = "## Test steering instructions\n- Run dotnet test";

        // Wait for the steering textarea to appear
        await Page.WaitForSelectorAsync("[data-testid='behaviour-card'] textarea", new() { Timeout = 5_000 });
        await repoPage.SetSteeringContentAsync(newSteering);

        // Save the Behaviour card
        await repoPage.SaveBehaviourAsync();

        // Assert: success feedback appears
        var successMsg = await repoPage.GetBehaviourSuccessMessageAsync();
        Assert.NotNull(successMsg);
        Assert.Contains("Behaviour saved", successMsg, StringComparison.OrdinalIgnoreCase);

        // Assert: reload the page and verify the steering content is persisted
        await repoPage.NavigateAsync(templateId);
        await Page.WaitForSelectorAsync("[data-testid='behaviour-card'] textarea", new() { Timeout = 5_000 });

        var savedSteering = await repoPage.GetSteeringContentAsync();
        Assert.NotNull(savedSteering);
        Assert.Contains("Test steering instructions", savedSteering);
    }
}
