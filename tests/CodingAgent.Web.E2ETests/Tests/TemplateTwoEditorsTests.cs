using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Pins the "last write wins" behaviour documented in <c>docs/internals/decisions.md</c> line 256:
/// the Repository page has no version check, so the second of two overlapping saves silently wins.
///
/// <para>
/// These tests use <see cref="InMemoryConfigurationStore"/> in place of Postgres, so no real
/// database row is written. In particular, Postgres row-level concurrency (<c>xmin</c>) never
/// runs here: simultaneous writes do not collide at the database level. What these tests prove is
/// the sequential UI and API-handler behaviour — that a stale browser window saves its value and
/// overwrites the earlier one, producing neither an error nor a conflict page.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class TemplateTwoEditorsTests : E2ETestBase
{
    public TemplateTwoEditorsTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task SecondSaveOfTheSameField_Wins_WithoutAnError()
    {
        const string templateId = "two-editors";
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = templateId,
            Name = "Two Editors",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            SteeringContent = "original",
        }, CancellationToken.None);

        // ── Arrange: open the page in two browser contexts ──────────────────
        // Page is editor A (from E2ETestBase). Editor B gets its own authenticated context.
        var browser = await Fixture.GetBrowserAsync();
        await using var contextB = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            StorageState = await Fixture.GetSignedInStorageStateAsync()
        });
        var pageB = await contextB.NewPageAsync();

        var repoA = new RepositoryPageObject(Page, BaseUrl);
        var repoB = new RepositoryPageObject(pageB, BaseUrl);

        await repoA.NavigateAsync(templateId);
        await repoB.NavigateAsync(templateId);

        // Both editors start on the original steering content.
        await Assertions.Expect(Page.Locator("[data-testid='behaviour-card'] textarea"))
            .ToHaveValueAsync("original", new() { Timeout = 5_000 });
        await Assertions.Expect(pageB.Locator("[data-testid='behaviour-card'] textarea"))
            .ToHaveValueAsync("original", new() { Timeout = 5_000 });

        // ── Act: editor A saves "from A" ─────────────────────────────────────
        await repoA.SetSteeringContentAsync("from A");
        await repoA.SaveBehaviourAsync();

        // ── Assert: A's save succeeded. Use a Playwright retry assertion to be resilient to
        // the ~4 s auto-clear (QuerySelectorAsync is point-in-time; a GC pause could race it).
        await Assertions.Expect(Page.Locator(".settings-status.status-success"))
            .ToContainTextAsync("Behaviour saved", new() { Timeout = 5_000 });

        var templates = await Fixture.ConfigStore.LoadTemplatesForProjectAsync(
            WellKnownIds.DefaultProjectId, CancellationToken.None);
        Assert.Equal("from A", templates.Single(t => t.Id == templateId).SteeringContent);

        // B's textarea still shows "original" — B is not told about A's save.
        await Assertions.Expect(pageB.Locator("[data-testid='behaviour-card'] textarea"))
            .ToHaveValueAsync("original", new() { Timeout = 2_000 });

        // ── Act: editor B saves "from B" (from its stale page) ───────────────
        await repoB.SetSteeringContentAsync("from B");
        await repoB.SaveBehaviourAsync();

        // ── Assert: B's save succeeded. Use a Playwright retry assertion for the same reason.
        await Assertions.Expect(pageB.Locator(".settings-status.status-success"))
            .ToContainTextAsync("Behaviour saved", new() { Timeout = 5_000 });

        // No error banner on B's page.
        await Assertions.Expect(pageB.Locator("[data-testid='behaviour-card'] .status-error"))
            .ToHaveCountAsync(0, new() { Timeout = 2_000 });

        templates = await Fixture.ConfigStore.LoadTemplatesForProjectAsync(
            WellKnownIds.DefaultProjectId, CancellationToken.None);
        Assert.Equal("from B", templates.Single(t => t.Id == templateId).SteeringContent);

        // ── Act: A navigates to the page again ────────────────────────────────
        await repoA.NavigateAsync(templateId);

        // ── Assert: A now sees B's value ──────────────────────────────────────
        await Assertions.Expect(Page.Locator("[data-testid='behaviour-card'] textarea"))
            .ToHaveValueAsync("from B", new() { Timeout = 5_000 });
    }
}
