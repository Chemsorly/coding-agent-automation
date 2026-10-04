using System.Text.Json;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for Settings → Data Management (ConfigImportExportSection.razor).
///
/// Covers the four scenarios from issue #3105:
///   1. Export — Downloads a valid JSON bundle containing the seeded config keys.
///   2. Import round-trip — Export state, mutate, re-import, verify original state restored.
///   3. Bad file — Malformed or empty JSON shows an error; config unchanged.
///   4. Run history untouched — Seeded run history survives an import.
///
/// Downloaded files are written to a per-test temp directory and deleted in DisposeAsync.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class DataManagementExportImportTests : IAsyncLifetime
{
    // ── Infrastructure ────────────────────────────────────────────────────────────

    private readonly E2EFixture _fixture;
    private IBrowserContext? _context;
    // TODO [WARNING]: Declare as IPage? instead of IPage null! to make nullability intent explicit.
    // If NewPageAsync() throws before _page is assigned, DisposeAsync's `_page is not null` guard
    // still works at runtime but only by coincidence; the null! suppression is misleading.
    private IPage _page = null!;

    /// <summary>Temporary directory for downloaded files; cleaned up in DisposeAsync.</summary>
    private string _tempDir = null!;

    public DataManagementExportImportTests(E2EFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        // Reset all state between tests (same as E2ETestBase)
        await _fixture.ResetAllAsync();

        // Create temp dir for downloads
        _tempDir = Path.Combine(Path.GetTempPath(), $"e2e-dm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Guard: verify DI replacement worked
        // TODO [WARNING]: GetRequiredService<IProviderFactory>() resolves from the root DI
        // container. If IProviderFactory is registered as Scoped, this creates a captive dependency
        // that lives for the entire test run (scoped-in-singleton anti-pattern). To validate
        // per-request DI wiring, perform this check inside a created scope instead.
        var factory = _fixture.Factory.Services.GetRequiredService<IProviderFactory>();
        if (factory is not FakeProviderFactory)
            throw new InvalidOperationException(
                $"DI replacement failed: IProviderFactory resolved as {factory.GetType().Name} instead of FakeProviderFactory");

        // Create a new browser context with downloads enabled, pointing to our temp dir
        var browser = await _fixture.GetBrowserAsync();
        _context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            AcceptDownloads = true,
            StorageState = await _fixture.GetSignedInStorageStateAsync()
        });
        await E2ETestBase.StubExternalFontsAsync(_context);
        _page = await _context.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        // Screenshot on cleanup
        if (_page is not null)
        {
            try
            {
                var screenshotDir = Path.Combine("TestResults", "screenshots");
                Directory.CreateDirectory(screenshotDir);
                var path = Path.Combine(screenshotDir, $"DataManagement_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
                await _page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
            }
            catch { /* ignore screenshot failures */ }
        }

        if (_context is not null)
            await _context.DisposeAsync();

        // Delete downloaded temp files
        if (_tempDir is not null && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* ignore cleanup failures */ }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string BaseUrl => _fixture.ServerAddress;

    private async Task NavigateToDataManagementAsync()
    {
        var settingsPage = new SettingsPage(_page, BaseUrl);
        await settingsPage.NavigateAsync();
        await settingsPage.SelectTreeNodeAsync("Import / Export");
        var section = new DataManagementSectionHelper(_page);
        await section.WaitForSectionAsync();
    }

    // ── Scenario 1: Export ────────────────────────────────────────────────────────

    /// <summary>
    /// Click "Download Config". Playwright captures a download. The downloaded file is valid JSON
    /// containing the expected top-level keys (pipelineConfig, providerConfigs, agentProfiles,
    /// qualityGateConfigs, reviewerConfigs, projects, jobTemplates) and the seeded provider.
    /// </summary>
    [Fact]
    public async Task Export_DownloadsValidJsonBundle_ContainingSeededConfig()
    {
        await NavigateToDataManagementAsync();
        var section = new DataManagementSectionHelper(_page);

        var downloadPath = Path.Combine(_tempDir, "export-scenario1.json");

        // Act: click Download Config and capture the download
        // TODO [WARNING]: This duplicates the download-click logic already in
        // DataManagementSectionHelper.ClickDownloadAndSaveAsync. The same duplication exists in
        // ImportRoundTrip_RestoresOriginalState_AfterMutation and Import_RunHistoryUntouched_AfterImport.
        // Consolidate to the helper so selector/logic changes only need one update.
        var download = await _page.RunAndWaitForDownloadAsync(
            async () => await _page.Locator("button:has-text('Download Config')").ClickAsync(),
            new() { Timeout = 15_000 });

        await download.SaveAsAsync(downloadPath);

        try
        {
            // Assert: the file exists and is non-empty
            Assert.True(File.Exists(downloadPath), "Downloaded file must exist");
            var bytes = await File.ReadAllBytesAsync(downloadPath);
            Assert.True(bytes.Length > 0, "Downloaded file must not be empty");

            // Assert: valid JSON
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            using var doc = JsonDocument.Parse(json);

            // Assert: required top-level keys present
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("pipelineConfig", out _) ||
                        root.TryGetProperty("PipelineConfig", out _),
                "Bundle must contain pipelineConfig");

            Assert.True(root.TryGetProperty("providerConfigs", out _) ||
                        root.TryGetProperty("ProviderConfigs", out _),
                "Bundle must contain providerConfigs");

            Assert.True(root.TryGetProperty("agentProfiles", out _) ||
                        root.TryGetProperty("AgentProfiles", out _),
                "Bundle must contain agentProfiles");

            Assert.True(root.TryGetProperty("qualityGateConfigs", out _) ||
                        root.TryGetProperty("QualityGateConfigs", out _),
                "Bundle must contain qualityGateConfigs");

            Assert.True(root.TryGetProperty("reviewerConfigs", out _) ||
                        root.TryGetProperty("ReviewerConfigs", out _),
                "Bundle must contain reviewerConfigs");

            Assert.True(root.TryGetProperty("projects", out _) ||
                        root.TryGetProperty("Projects", out _),
                "Bundle must contain projects");

            Assert.True(root.TryGetProperty("jobTemplates", out _) ||
                        root.TryGetProperty("JobTemplates", out _),
                "Bundle must contain jobTemplates");

            // Assert: seeded providers are in the export (check providerConfigs array length)
            // TODO [WARNING]: This only asserts that the array is non-empty, not that the seeded
            // provider's identity (display name, kind, ID) is present. The issue requires "containing
            // the seeded template and provider" — a targeted assertion against a known provider name
            // is needed to prove the seeded data was actually exported rather than any arbitrary entry.
            var providerConfigsKey = root.TryGetProperty("providerConfigs", out var providerConfigsEl)
                ? providerConfigsEl
                : root.GetProperty("ProviderConfigs");

            Assert.True(providerConfigsKey.GetArrayLength() > 0,
                "providerConfigs must contain the seeded providers");
        }
        finally
        {
            // Clean up the downloaded file
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);
        }
    }

    // ── Scenario 2: Import round trip ──────────────────────────────────────────

    /// <summary>
    /// Export the current state (seeded template + provider).
    /// Add a quality gate config (mutation).
    /// The overwrite warning is visible before clicking Import.
    /// Import the previously exported file.
    /// Verify the extra quality gate config is gone and the original state is restored.
    /// Verify the Settings and pipeline pages reflect the imported state without a manual reload.
    /// </summary>
    [Fact]
    public async Task ImportRoundTrip_RestoresOriginalState_AfterMutation()
    {
        await NavigateToDataManagementAsync();
        var section = new DataManagementSectionHelper(_page);

        // Step 1: Export the current (seeded) state
        var exportPath = Path.Combine(_tempDir, "round-trip-export.json");
        var download = await _page.RunAndWaitForDownloadAsync(
            async () => await _page.Locator("button:has-text('Download Config')").ClickAsync(),
            new() { Timeout = 15_000 });
        await download.SaveAsAsync(exportPath);

        try
        {
            Assert.True(File.Exists(exportPath), "Export file must exist for round-trip test");

            // Step 2: Mutate state — add an extra quality gate config
            await _fixture.ConfigStore.SaveQualityGateConfigAsync(new QualityGateConfiguration
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = "Extra QG That Should Disappear",
                CompilationCommand = "echo",
                CompilationArguments = ["extra"],
                TestCommand = "echo",
                TestArguments = ["extra"],
                Enabled = true
            }, CancellationToken.None);

            // Also modify a setting (MaxRetries → 9)
            var originalConfig = await _fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
            await _fixture.ConfigStore.SavePipelineConfigAsync(
                originalConfig with { MaxRetries = 9 }, CancellationToken.None);

            // Verify mutation is visible
            var qgAfterMutation = await _fixture.ConfigStore.LoadQualityGateConfigsAsync(CancellationToken.None);
            Assert.Contains(qgAfterMutation, q => q.DisplayName == "Extra QG That Should Disappear");

            // Step 3: Navigate back to Data Management (page already there, but refresh state)
            await NavigateToDataManagementAsync();

            // Step 4: Select the exported file
            await section.SelectImportFileAsync(exportPath);

            // Assert: Import button is visible (file selected)
            Assert.True(await section.IsImportButtonVisibleAsync(),
                "Import button must appear after selecting a file");

            // Assert: Overwrite warning is visible before clicking Import
            Assert.True(await section.IsOverwriteWarningVisibleAsync(),
                "Overwrite warning must be visible before clicking Import");

            // Step 5: Click Import
            await section.ClickImportAsync();

            // Assert: success message shown
            Assert.True(await section.IsSuccessMessageVisibleAsync(),
                "Success message must appear after successful import");

            // Assert: extra quality gate config is gone (import was destructive)
            var qgAfterImport = await _fixture.ConfigStore.LoadQualityGateConfigsAsync(CancellationToken.None);
            Assert.DoesNotContain(qgAfterImport, q => q.DisplayName == "Extra QG That Should Disappear");

            // Assert: original seeded quality gate is restored
            // TODO [WARNING]: The name "E2E Quality Gate" is hard-coded and will produce a confusing
            // failure if the seeded fixture changes. Capture the original quality gate list before
            // mutation and assert against those entries instead of a magic string literal.
            Assert.Contains(qgAfterImport, q => q.DisplayName == "E2E Quality Gate");

            // Assert: setting is restored (MaxRetries should be back to 3)
            // TODO [WARNING]: Magic literal 3 hard-codes the seeded default. If InMemoryConfigurationStore's
            // default changes, this assertion fails with a misleading number mismatch. Assert against
            // originalConfig.MaxRetries (captured before the mutation at step 2) instead.
            var configAfterImport = await _fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
            Assert.Equal(3, configAfterImport.MaxRetries);

            // TODO [WARNING]: Projects and job templates are not asserted after import. The issue
            // requires the round-trip to restore "the seeded template and provider". A regression in
            // project or template restore in the fake's ImportConfigAsync would not be caught here.
            // Assert that the original project IDs and template IDs are present after import.

            // Assert: page reflects imported state without reload — navigate to Settings to verify
            // the provider list is still there (UI auto-refresh on import)
            var settingsPage = new SettingsPage(_page, BaseUrl);
            await settingsPage.SelectTreeNodeAsync("Quality Gate Configs");
            // TODO [WARNING]: page.ContentAsync() returns the full HTML (navigation links, scripts,
            // tooltips, hidden elements). A match against "E2E Quality Gate" anywhere in the DOM
            // could produce a false positive. Use a targeted locator (specific list/table row) for
            // a more reliable UI refresh assertion.
            var markup = await _page.ContentAsync();
            Assert.Contains("E2E Quality Gate", markup);
            Assert.DoesNotContain("Extra QG That Should Disappear", markup);
        }
        finally
        {
            if (File.Exists(exportPath))
                File.Delete(exportPath);
        }
    }

    // ── Scenario 3: Bad file ──────────────────────────────────────────────────

    /// <summary>
    /// Uploading malformed JSON shows an error and the configuration is unchanged.
    /// </summary>
    [Fact]
    public async Task Import_MalformedJson_ShowsError_ConfigUnchanged()
    {
        await NavigateToDataManagementAsync();
        var section = new DataManagementSectionHelper(_page);

        // Create a malformed JSON file
        var badFilePath = Path.Combine(_tempDir, "bad-import.json");
        await File.WriteAllTextAsync(badFilePath, "{ this is not valid json }");

        try
        {
            // Snapshot original state across all entity types before the bad import attempt
            var originalQgIds = (await _fixture.ConfigStore.LoadQualityGateConfigsAsync(CancellationToken.None))
                .Select(q => q.Id).OrderBy(id => id).ToList();
            var originalProviderIds = (await _fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None))
                .Concat(await _fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None))
                .Concat(await _fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None))
                .Select(p => p.Id).OrderBy(id => id).ToList();
            var originalProfileIds = (await _fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None))
                .Select(p => p.Id).OrderBy(id => id).ToList();
            var originalProjectIds = (await _fixture.ConfigStore.LoadProjectsAsync(CancellationToken.None))
                .Select(p => p.Id).OrderBy(id => id).ToList();
            // TODO [WARNING]: reviewerConfigs are not snapshotted or asserted here. ImportConfigAsync
            // clears reviewer configs before projects/templates. A partial-clear bug that corrupts
            // reviewers would be invisible to this test. Add a snapshot of reviewer IDs and a
            // corresponding Assert.Equal after the import attempt.

            // Act: select malformed file
            await section.SelectImportFileAsync(badFilePath);
            Assert.True(await section.IsImportButtonVisibleAsync(),
                "Import button must appear after selecting a file");

            // Click Import
            await section.ClickImportAsync();

            // Assert: error message shown
            Assert.True(await section.IsErrorMessageVisibleAsync(),
                "Error message must appear after importing malformed JSON");

            // Assert: all entity types unchanged — check full identity set to detect partial-clear
            // from the non-atomic import path (providers cleared first, then profiles, then QGs)
            var qgAfterBadImport = (await _fixture.ConfigStore.LoadQualityGateConfigsAsync(CancellationToken.None))
                .Select(q => q.Id).OrderBy(id => id).ToList();
            Assert.Equal(originalQgIds, qgAfterBadImport);

            var providerAfterBadImport = (await _fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None))
                .Concat(await _fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None))
                .Concat(await _fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None))
                .Select(p => p.Id).OrderBy(id => id).ToList();
            Assert.Equal(originalProviderIds, providerAfterBadImport);

            var profileAfterBadImport = (await _fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None))
                .Select(p => p.Id).OrderBy(id => id).ToList();
            Assert.Equal(originalProfileIds, profileAfterBadImport);

            var projectAfterBadImport = (await _fixture.ConfigStore.LoadProjectsAsync(CancellationToken.None))
                .Select(p => p.Id).OrderBy(id => id).ToList();
            Assert.Equal(originalProjectIds, projectAfterBadImport);
        }
        finally
        {
            if (File.Exists(badFilePath))
                File.Delete(badFilePath);
        }
    }

    /// <summary>
    /// Uploading an empty file shows an error and the configuration is unchanged.
    /// </summary>
    [Fact]
    public async Task Import_EmptyFile_ShowsError_ConfigUnchanged()
    {
        await NavigateToDataManagementAsync();
        var section = new DataManagementSectionHelper(_page);

        // Create an empty file
        var emptyFilePath = Path.Combine(_tempDir, "empty-import.json");
        await File.WriteAllTextAsync(emptyFilePath, "");

        try
        {
            // Snapshot original state across all entity types before the bad import attempt
            var originalQgIds = (await _fixture.ConfigStore.LoadQualityGateConfigsAsync(CancellationToken.None))
                .Select(q => q.Id).OrderBy(id => id).ToList();
            var originalProviderIds = (await _fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None))
                .Concat(await _fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None))
                .Concat(await _fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None))
                .Select(p => p.Id).OrderBy(id => id).ToList();
            var originalProfileIds = (await _fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None))
                .Select(p => p.Id).OrderBy(id => id).ToList();
            var originalProjectIds = (await _fixture.ConfigStore.LoadProjectsAsync(CancellationToken.None))
                .Select(p => p.Id).OrderBy(id => id).ToList();
            // TODO [WARNING]: reviewerConfigs are not snapshotted or asserted here. ImportConfigAsync
            // clears reviewer configs before projects/templates. A partial-clear bug that corrupts
            // reviewers would be invisible to this test. Add a snapshot of reviewer IDs and a
            // corresponding Assert.Equal after the import attempt.

            await section.SelectImportFileAsync(emptyFilePath);
            Assert.True(await section.IsImportButtonVisibleAsync(),
                "Import button must appear after selecting an empty file");

            await section.ClickImportAsync();

            // Assert: error message shown
            Assert.True(await section.IsErrorMessageVisibleAsync(),
                "Error message must appear after importing an empty file");

            // Assert: all entity types unchanged — check full identity set to detect partial-clear
            // from the non-atomic import path (providers cleared first, then profiles, then QGs)
            var qgAfterBadImport = (await _fixture.ConfigStore.LoadQualityGateConfigsAsync(CancellationToken.None))
                .Select(q => q.Id).OrderBy(id => id).ToList();
            Assert.Equal(originalQgIds, qgAfterBadImport);

            var providerAfterBadImport = (await _fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None))
                .Concat(await _fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None))
                .Concat(await _fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None))
                .Select(p => p.Id).OrderBy(id => id).ToList();
            Assert.Equal(originalProviderIds, providerAfterBadImport);

            var profileAfterBadImport = (await _fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None))
                .Select(p => p.Id).OrderBy(id => id).ToList();
            Assert.Equal(originalProfileIds, profileAfterBadImport);

            var projectAfterBadImport = (await _fixture.ConfigStore.LoadProjectsAsync(CancellationToken.None))
                .Select(p => p.Id).OrderBy(id => id).ToList();
            Assert.Equal(originalProjectIds, projectAfterBadImport);
        }
        finally
        {
            if (File.Exists(emptyFilePath))
                File.Delete(emptyFilePath);
        }
    }

    // ── Scenario 4: Run history untouched ──────────────────────────────────────

    /// <summary>
    /// Seeded run history is still present after an import.
    /// The import endpoint explicitly excludes run history from the destructive clear.
    /// </summary>
    [Fact]
    public async Task Import_RunHistoryUntouched_AfterImport()
    {
        // Seed a run history entry before the import
        var historyRunId = Guid.NewGuid().ToString();
        await _fixture.HistoryService.AddRunSummaryAsync(new PipelineRunSummary
        {
            RunId = historyRunId,
            IssueIdentifier = new IssueIdentifier("history-issue-3105"),
            IssueTitle = "Seeded history for scenario 4",
            FinalStep = PipelineStep.Completed,
#pragma warning disable CS0618
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1),
#pragma warning restore CS0618
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        // Verify it's there before import
        var historyBefore = await _fixture.HistoryService.GetRunHistoryAsync();
        Assert.Contains(historyBefore, r => r.RunId == historyRunId);

        // Navigate and export
        await NavigateToDataManagementAsync();
        var section = new DataManagementSectionHelper(_page);

        var exportPath = Path.Combine(_tempDir, "history-test-export.json");
        var download = await _page.RunAndWaitForDownloadAsync(
            async () => await _page.Locator("button:has-text('Download Config')").ClickAsync(),
            new() { Timeout = 15_000 });
        await download.SaveAsAsync(exportPath);

        try
        {
            // Navigate back to Data Management and import the exported file
            await NavigateToDataManagementAsync();
            await section.SelectImportFileAsync(exportPath);
            await section.ClickImportAsync();

            Assert.True(await section.IsSuccessMessageVisibleAsync(),
                "Import must succeed before checking run history");

            // Assert: run history still present after import
            var historyAfter = await _fixture.HistoryService.GetRunHistoryAsync();
            Assert.Contains(historyAfter, r => r.RunId == historyRunId);
        }
        finally
        {
            if (File.Exists(exportPath))
                File.Delete(exportPath);
        }
    }
}
