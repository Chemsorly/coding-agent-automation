using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Verifies the not-found and invalid-ID error states on the Run, Repository, and Triage pages.
/// Each page must show a friendly message rather than a blank page or Blazor error.
/// Each test also asserts that #blazor-error-ui is not visible (no unhandled render exception).
/// Uses the admin session (E2ETestBase). No RunDetailPage.NavigateAsync or
/// RepositoryPageObject.NavigateAsync — both wait for h1 which is absent in error states.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class NotFoundPagesTests : E2ETestBase
{
    public NotFoundPagesTests(E2EFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunPage_NonGuidId_SaysInvalid()
    {
        // RunPage.razor:650 sets _error = "Invalid run id." when the route value cannot be
        // parsed as a Guid. RunPage.razor:40 renders @(_error ?? "Run not found.") in .cockpit-empty.
        await Page.GotoCockpitPageAsync($"{BaseUrl}/runs/not-a-guid");

        await Assertions.Expect(Page.Locator("[data-testid='run-page'] .cockpit-empty"))
            .ToHaveTextAsync("Invalid run id.");
        await Assertions.Expect(Page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task RunPage_UnknownGuid_SaysNotFound()
    {
        // A valid GUID that does not match any run in the system.
        // The API returns null (404 → FindAsync → null) and RunPage shows "Run not found."
        await Page.GotoCockpitPageAsync($"{BaseUrl}/runs/{Guid.NewGuid()}");

        await Assertions.Expect(Page.Locator("[data-testid='run-page'] .cockpit-empty"))
            .ToHaveTextAsync("Run not found.");
        await Assertions.Expect(Page.Locator(".cockpit-back")).ToBeVisibleAsync();
        await Assertions.Expect(Page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task RepositoryPage_UnknownId_SaysNotFound()
    {
        // "no-such-template" is a string ID that matches no seeded template.
        // RepositoryPageService.CanView() returns false → page renders "Template not found."
        // GotoCockpitPageAsync may return during the .monitoring-empty loading state;
        // ToHaveTextAsync polls until the not-found text is present.
        await Page.GotoCockpitPageAsync($"{BaseUrl}/pipelines/no-such-template");

        await Assertions.Expect(Page.Locator(".monitoring-empty"))
            .ToHaveTextAsync("Template not found.");
        await Assertions.Expect(Page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task TriagePage_UnknownGuid_SaysNotFound()
    {
        // TriagePage.razor:34 renders [data-testid='triage-not-found'] with
        // @(_error ?? "Triage not found.") when UiTriageClient.GetAsync returns null.
        // The route constraint is /triage/{Id:guid} — only valid GUIDs reach the handler.
        await Page.GotoCockpitPageAsync($"{BaseUrl}/triage/{Guid.NewGuid()}");

        await Assertions.Expect(Page.Locator("[data-testid='triage-not-found']"))
            .ToHaveTextAsync("Triage not found.");
        await Assertions.Expect(Page.Locator("#blazor-error-ui")).Not.ToBeVisibleAsync();
    }
}
