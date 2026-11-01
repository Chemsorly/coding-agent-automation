using System.Text;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the GitHub App provider form: client-side validation, Fetch Repos paths,
/// Save paths (check refusal, API failure), and masked-key edit round-trip.
///
/// All API calls to the provider check and repository endpoints are intercepted via
/// <see cref="IPage.RouteAsync"/> — no external network traffic is made. The real
/// <c>PUT /api/ui/settings/provider-configs</c> endpoint is left unstubbed except in test 7,
/// which intentionally makes it fail.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class GitHubAppProviderFormTests : E2ETestBase
{
    private int _checkCallCount;
    private int _repoCallCount;

    public GitHubAppProviderFormTests(E2EFixture fixture) : base(fixture) { }

    // ── Private helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Navigates to Settings and opens the "Add Issue Provider" form (GitHub type by default).
    /// </summary>
    private async Task OpenAddFormAsync()
    {
        var sp = new SettingsPage(Page, BaseUrl);
        await sp.NavigateAsync();
        await sp.ClickAddIssueProviderAsync();
    }

    /// <summary>
    /// Fills the input inside the form-group whose label exactly matches <paramref name="label"/>.
    /// GitHub App form inputs all use @bind:event="oninput" so FillAsync fires the binding immediately.
    /// </summary>
    private Task FillAsync(string label, string value) =>
        Page.Locator($"div.provider-form div.form-group:has(label:text-is('{label}')) input").FillAsync(value);

    /// <summary>The validation message element inside the provider form.</summary>
    private ILocator Message => Page.Locator("div.provider-form .settings-status").First;

    /// <summary>
    /// Registers a route that intercepts the check endpoint and responds with the given result.
    /// Increments <see cref="_checkCallCount"/> on each hit.
    /// Must be called before <see cref="OpenAddFormAsync"/> (route-before-click rule).
    /// </summary>
    private async Task StubCheckAsync(bool success, string message)
    {
        // Use JsonSerializer to properly escape message, handling quotes and backslashes.
        var escapedMessage = System.Text.Json.JsonSerializer.Serialize(message);
        var body = $"{{\"success\":{(success ? "true" : "false")},\"message\":{escapedMessage}}}";
        await Page.RouteAsync("**/api/ui/settings/provider-configs/check", async route =>
        {
            _checkCallCount++;
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = body,
            });
        });
    }

    /// <summary>
    /// Registers a route that intercepts the repositories endpoint and responds with one test repo.
    /// Increments <see cref="_repoCallCount"/> on each hit.
    /// Must be called before <see cref="OpenAddFormAsync"/> (route-before-click rule).
    /// </summary>
    private async Task StubRepositoriesAsync()
    {
        const string body = "[{\"fullName\":\"e2e-org/e2e-repo\",\"owner\":\"e2e-org\",\"name\":\"e2e-repo\"}]";
        await Page.RouteAsync("**/api/ui/settings/provider-configs/repositories", async route =>
        {
            _repoCallCount++;
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "application/json",
                Body = body,
            });
        });
    }

    /// <summary>
    /// Uploads a synthetic PEM file via the file input and waits for the key-status indicator.
    /// </summary>
    private async Task SetKeyAsync()
    {
        await Page.Locator("div.provider-form input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "app.pem",
            MimeType = "application/x-pem-file",
            Buffer = Encoding.UTF8.GetBytes("pem-content"),
        });
        await Assertions.Expect(Page.Locator(".key-status")).ToContainTextAsync("Key loaded");
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves without filling required fields and verifies the client-side validation errors appear
    /// in the documented order — without ever hitting the check API.
    /// </summary>
    [Fact]
    public async Task Save_MissingFields_AreRefusedInOrder_WithoutCallingTheApi()
    {
        // Arrange: stub the check before navigating so it would count calls if invoked
        await StubCheckAsync(true, "ok");
        await OpenAddFormAsync();

        // ── Click 1: no display name ────────────────────────────────────────
        await Page.Locator("div.form-buttons button.btn-save").ClickAsync();
        await Assertions.Expect(Message).ToContainTextAsync("Display Name is required.");
        await Assertions.Expect(Message).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("status-error"));

        // ── Click 2: no owner/repo ──────────────────────────────────────────
        await FillAsync("Display Name", "GitHub U05 T1");
        await Page.Locator("div.form-buttons button.btn-save").ClickAsync();
        await Assertions.Expect(Message).ToContainTextAsync("Owner and Repository are required.");
        await Assertions.Expect(Message).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("status-error"));

        // ── Click 3: non-numeric Installation ID ────────────────────────────
        await FillAsync("Owner", "e2e-org");
        await FillAsync("Repository", "e2e-repo");
        await FillAsync("Installation ID", "abc");
        await Page.Locator("div.form-buttons button.btn-save").ClickAsync();
        await Assertions.Expect(Message).ToContainTextAsync("Installation ID must be a valid number.");
        await Assertions.Expect(Message).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("status-error"));

        // Assert: check endpoint was never called
        Assert.Equal(0, _checkCallCount);

        // Assert: nothing was persisted
        var configs = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        Assert.DoesNotContain(configs, p => p.DisplayName == "GitHub U05 T1");
    }

    /// <summary>
    /// Clicks Fetch Repos with missing required fields, then with a non-numeric Installation ID.
    /// Neither attempt should reach the API.
    /// </summary>
    [Fact]
    public async Task FetchRepos_MissingFields_AreRefused()
    {
        // Stub the repositories endpoint defensively: if client-side validation were bypassed by
        // a regression, this prevents an inadvertent live network call and makes the failure fast.
        await StubRepositoriesAsync();
        await OpenAddFormAsync();

        // Click with everything empty: all three fields missing
        await Page.Locator("div.provider-form button.btn-edit").ClickAsync();
        await Assertions.Expect(Message).ToContainTextAsync("Enter Client ID, Installation ID, and Private Key first.");

        // Fill Client ID and Installation ID (non-numeric) + key, then click again
        await FillAsync("Client ID", "Iv1.abc");
        await FillAsync("Installation ID", "abc");
        await SetKeyAsync();
        await Page.Locator("div.provider-form button.btn-edit").ClickAsync();
        await Assertions.Expect(Message).ToContainTextAsync("Installation ID must be a valid number.");

        // Assert: repositories endpoint was never called (validation stopped before the API)
        Assert.Equal(0, _repoCallCount);
    }

    /// <summary>
    /// Fetches repos successfully, selects the returned repo from the dropdown, saves, and verifies
    /// the store has the correct settings including the Base64-encoded private key.
    /// </summary>
    [Fact]
    public async Task FetchRepos_Success_OffersARepository_AndSaveStoresIt()
    {
        // Arrange: stubs must be registered before navigation
        await StubRepositoriesAsync();
        await StubCheckAsync(true, "Credentials valid");
        await OpenAddFormAsync();

        // Fill required fields
        await FillAsync("Display Name", "GitHub U05");
        await FillAsync("Client ID", "Iv1.abc");
        await FillAsync("Installation ID", "78901234");
        await SetKeyAsync();

        // Click Fetch Repos
        await Page.Locator("div.provider-form button.btn-edit").ClickAsync();

        // The success message contains the repo count (FetchRepos success path in IssueProviderSection)
        await Assertions.Expect(Message).ToContainTextAsync("Credentials valid — 1 repositories found");
        await Assertions.Expect(Message).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("status-success"));

        // Select the returned repo from the dropdown that appears after a successful fetch
        await Page.Locator("div.provider-form div.form-group:has(label:text-is('Repository')) select")
            .SelectOptionAsync("e2e-org/e2e-repo");

        // Save
        await Page.Locator("div.form-buttons button.btn-save").ClickAsync();

        // Assert: the Settings page shows its save confirmation
        var sp = new SettingsPage(Page, BaseUrl);
        await WaitUntilAsync(
            async () => await sp.IsStatusMessageVisibleAsync("Issue provider saved."),
            TimeSpan.FromSeconds(10));

        // Dismiss any modal that opens after a successful issue-provider save
        await sp.DismissModalIfPresentAsync();

        // Assert: store holds the provider with correct settings
        var configs = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var saved = configs.Single(p => p.DisplayName == "GitHub U05");
        Assert.Equal("GitHub", saved.ProviderType);
        Assert.Equal("e2e-org", saved.Settings[ProviderSettingKeys.Owner]);
        Assert.Equal("e2e-repo", saved.Settings[ProviderSettingKeys.Repo]);
        Assert.Equal("78901234", saved.Settings[ProviderSettingKeys.InstallationId]);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("pem-content")),
            saved.Settings[ProviderSettingKeys.PrivateKeyBase64]);

        // Assert: provider card shows the owner/repo (scope to the "GitHub U05" card specifically;
        // the pre-seeded "E2E Issue Provider" is also present and is the first card on the page)
        await Assertions.Expect(
            Page.Locator("div.provider-card:has(strong:has-text('GitHub U05'))"))
            .ToContainTextAsync("e2e-org/e2e-repo");
    }

    /// <summary>
    /// Stubs the repositories endpoint with a 400 and verifies the API's reason is shown.
    /// </summary>
    [Fact]
    public async Task FetchRepos_ApiRefuses_ShowsTheReason()
    {
        // Stub before navigating (rule: route before clicking)
        await Page.RouteAsync("**/api/ui/settings/provider-configs/repositories", async route =>
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 400,
                ContentType = "application/json",
                Body = "\"Bad credentials\"",   // JSON string — UiApiClient unwraps it
            });
        });

        await OpenAddFormAsync();

        // Fill enough fields so the client-side validation passes
        await FillAsync("Client ID", "Iv1.abc");
        await FillAsync("Installation ID", "12345678");
        await SetKeyAsync();

        await Page.Locator("div.provider-form button.btn-edit").ClickAsync();

        // UiApiClient maps 400 → InvalidOperationException(reason); component shows ex.Message
        await Assertions.Expect(Message).ToContainTextAsync("Bad credentials");
        await Assertions.Expect(Message).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("status-error"));
    }

    /// <summary>
    /// Stubs the repositories endpoint with a 500 and verifies the generic error prefix.
    /// </summary>
    [Fact]
    public async Task FetchRepos_ApiFails_ShowsAnError()
    {
        await Page.RouteAsync("**/api/ui/settings/provider-configs/repositories", async route =>
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 500,
                ContentType = "application/json",
                Body = "\"internal error\"",
                // Note: for HTTP 500, UiApiClient throws HttpRequestException (not InvalidOperationException).
                // The body is NOT extracted — component formats any Exception as the prefix below.
            });
        });

        await OpenAddFormAsync();

        await FillAsync("Client ID", "Iv1.abc");
        await FillAsync("Installation ID", "12345678");
        await SetKeyAsync();

        await Page.Locator("div.provider-form button.btn-edit").ClickAsync();

        // Component catch block for Exception: "Failed to fetch repositories: {ex.Message}"
        await Assertions.Expect(Message).ToContainTextAsync("Failed to fetch repositories:");
        await Assertions.Expect(Message).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("status-error"));
    }

    /// <summary>
    /// Stubs the check to fail with "App not installed" and verifies the form stays open with no provider saved.
    ///
    /// Mutation-check target: changing <c>if (!success)</c> to <c>if (false)</c> in
    /// <c>IssueProviderSection.razor SaveProvider</c> would allow the save to proceed, making the
    /// store assertion fail.
    /// </summary>
    [Fact]
    public async Task Save_CheckRefuses_ShowsTheMessage_AndDoesNotStore()
    {
        await StubCheckAsync(false, "App not installed");
        await OpenAddFormAsync();

        // Fill a complete form
        await FillAsync("Display Name", "GitHub U05 T6");
        await FillAsync("Client ID", "Iv1.abc");
        await FillAsync("Installation ID", "12345678");
        await SetKeyAsync();
        await FillAsync("Owner", "e2e-org");
        await FillAsync("Repository", "e2e-repo");

        await Page.Locator("div.form-buttons button.btn-save").ClickAsync();

        // Assert: the check message is shown as an error
        await Assertions.Expect(Message).ToContainTextAsync("App not installed");
        await Assertions.Expect(Message).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("status-error"));

        // Assert: the form is still visible (save was aborted)
        await Assertions.Expect(Page.Locator("div.provider-form")).ToBeVisibleAsync();

        // Assert: nothing was persisted
        var configs = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        Assert.DoesNotContain(configs, p => p.DisplayName == "GitHub U05 T6");
    }

    /// <summary>
    /// Makes the PUT endpoint return 500 and verifies the form keeps its content.
    /// </summary>
    [Fact]
    public async Task Save_ApiFails_ShowsTheError_KeepsTheForm()
    {
        // Stub check so it passes
        await StubCheckAsync(true, "ok");

        // Stub the provider-configs route: PUT → 500, everything else falls through to the real handler
        await Page.RouteAsync("**/api/ui/settings/provider-configs", async route =>
        {
            if (route.Request.Method == "PUT")
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 500 });
            }
            else
            {
                await route.FallbackAsync();
            }
        });

        await OpenAddFormAsync();

        const string displayName = "GitHub U05 T7";
        await FillAsync("Display Name", displayName);
        await FillAsync("Client ID", "Iv1.abc");
        await FillAsync("Installation ID", "12345678");
        await SetKeyAsync();
        await FillAsync("Owner", "e2e-org");
        await FillAsync("Repository", "e2e-repo");

        await Page.Locator("div.form-buttons button.btn-save").ClickAsync();

        // Component catch block for Exception: "Failed to save: {ex.Message}"
        await Assertions.Expect(Message).ToContainTextAsync("Failed to save:");

        // The Display Name input keeps its value (form stayed open)
        await Assertions.Expect(
            Page.Locator("div.provider-form div.form-group:has(label:text-is('Display Name')) input"))
            .ToHaveValueAsync(displayName);
    }

    /// <summary>
    /// Seeds a provider with a stored private key, opens its edit form (which shows the key as masked),
    /// renames it, saves, and verifies the stored key is preserved (not overwritten by the mask).
    /// </summary>
    [Fact]
    public async Task EditWithMaskedKey_KeepsTheStoredKey()
    {
        // Arrange: seed the provider directly in the config store
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "github-u05",
            DisplayName = "Masked U05",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.ApiUrl]           = ProviderSettingKeys.DefaultApiUrl,
                [ProviderSettingKeys.ClientId]         = "Iv1.abc",
                [ProviderSettingKeys.InstallationId]   = "123",
                [ProviderSettingKeys.PrivateKeyBase64] = "stored-key",
                [ProviderSettingKeys.Owner]            = "o",
                [ProviderSettingKeys.Repo]             = "r",
            },
        }, CancellationToken.None);

        // Stub the check before navigating — save will call it
        await StubCheckAsync(true, "ok");

        // Navigate and open the edit form
        var sp = new SettingsPage(Page, BaseUrl);
        await sp.NavigateAsync();
        await sp.ClickEditProviderAsync("Masked U05");

        // The form shows the key-status indicator because the masked key ("****") is non-empty
        await Assertions.Expect(Page.Locator(".key-status")).ToBeVisibleAsync();

        // Edit only the display name
        await FillAsync("Display Name", "Masked U05 renamed");

        // Save using the direct button click (not SettingsPage.ClickSaveAsync, which adds a fixed wait)
        await Page.Locator("div.form-buttons button.btn-save").ClickAsync();

        // Poll the store until the rename is committed
        await WaitUntilAsync(
            async () =>
            {
                var configs = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
                return configs.Any(p => p.DisplayName == "Masked U05 renamed");
            },
            TimeSpan.FromSeconds(10));

        // Assert: the private key was preserved (UiSecretMask.Restore replaces "****" with "stored-key")
        var allConfigs = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var renamed = allConfigs.Single(p => p.DisplayName == "Masked U05 renamed");
        Assert.Equal("stored-key", renamed.Settings[ProviderSettingKeys.PrivateKeyBase64]);

        await sp.DismissModalIfPresentAsync();
    }
}
