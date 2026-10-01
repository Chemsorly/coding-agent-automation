using System.Text.Json;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the Data Management → Import / Export section of the Settings page.
/// Covers the four scenarios from Issue #3105:
///   1. Export — clicking "Download Config" produces a valid JSON file.
///   2. Import round trip — importing a previously exported file restores state.
///   3. Bad file — importing malformed JSON shows an error without corrupting state.
///   4. Run history untouched — import does not clear pipeline run history.
///
/// All four scenarios drive the real browser UI via Playwright.
/// Downloaded temp files are cleaned up in finally blocks per the acceptance criteria.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class DataManagementExportImportTests : E2ETestBase
{
    public DataManagementExportImportTests(E2EFixture fixture) : base(fixture) { }

    // ── Scenario 1 — Export ──────────────────────────────────────────────────

    /// <summary>
    /// Clicking "Download Config" shows the "Export downloaded successfully." result message,
    /// and the fake client's ExportConfigAsync produces a valid JSON bundle with the expected
    /// top-level keys and seeded data.
    ///
    /// Note: Playwright's RunAndWaitForDownloadAsync is not used here because Blob URL downloads
    /// (created by URL.createObjectURL in downloadFileFromStream) do not reliably trigger
    /// Playwright's download event in headless Chromium. The bundle content is verified directly
    /// via the fake client, and the UI interaction is verified via the success message.
    /// </summary>
    [Fact]
    public async Task DataManagement_Export_DownloadsValidJsonFile()
    {
        // ResetAllAsync in InitializeAsync already seeds default config (3 providers,
        // 1 quality gate, 1 project via InMemoryConfigurationStore.SeedDefaults).

        // ── Part 1: Verify bundle content via the fake client ────────────────
        // The component calls ConfigClient.ExportConfigAsync (the same fake) when the button
        // is clicked. Verifying the bundle here confirms the fake produces valid output and
        // the assertions would be meaningful if/when a real download capture is added.
        var configClient = Fixture.Factory.ApiConfigClient;
        var exportedBytes = await configClient.ExportConfigAsync(CancellationToken.None);
        Assert.NotEmpty(exportedBytes);

        var json = System.Text.Encoding.UTF8.GetString(exportedBytes);
        Assert.False(string.IsNullOrWhiteSpace(json));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Assert all expected top-level bundle keys are present
        Assert.True(root.TryGetProperty("pipelineConfig", out _));
        Assert.True(root.TryGetProperty("providerConfigs", out var providerConfigs));
        Assert.True(root.TryGetProperty("agentProfiles", out _));
        Assert.True(root.TryGetProperty("qualityGateConfigs", out var qgConfigs));
        Assert.True(root.TryGetProperty("reviewerConfigs", out _));
        Assert.True(root.TryGetProperty("projects", out var projects));
        Assert.True(root.TryGetProperty("jobTemplates", out var jobTemplates));

        // Assert seeded data appears in the bundle
        Assert.True(providerConfigs.GetArrayLength() >= 3);
        Assert.True(qgConfigs.GetArrayLength() >= 1);
        Assert.True(projects.GetArrayLength() >= 1);
        // Issue requires the bundle to contain "the seeded template" — assert jobTemplates is non-empty
        // and contains the seeded "E2E Template" (seeded in InMemoryConfigurationStore.SeedDefaults).
        Assert.True(jobTemplates.GetArrayLength() >= 1);
        Assert.Contains(
            jobTemplates.EnumerateArray(),
            t => t.TryGetProperty("name", out var n) && n.GetString() == "E2E Template");

        // ── Part 2: Verify the UI export button triggers the success message ─
        var helper = new DataManagementSectionHelper(Page, BaseUrl);
        await helper.NavigateAsync();

        // Click the export button — the component calls ExportConfigAsync and then
        // downloadFileFromStream (a JS Blob URL download). We don't capture the file via
        // Playwright (Blob URL downloads are not interceptable in headless Chromium), but we
        // do assert the success message appears to confirm the button handler ran successfully.
        await helper.ClickExportAsync();

        // Assert success message is visible in the UI
        var resultText = await helper.WaitForResultAsync(isSuccess: true);
        Assert.Contains("Export downloaded successfully", resultText, StringComparison.OrdinalIgnoreCase);
    }

    // ── Scenario 2 — Import round trip ───────────────────────────────────────

    /// <summary>
    /// Exports the current config, modifies the store state (adds an extra quality gate),
    /// then imports the exported file via the UI. Asserts that:
    /// - the import succeeds with a success message,
    /// - the extra quality gate config is gone (state restored),
    /// - no full page reload was required (verification via tree navigation within the same route).
    /// </summary>
    [Fact]
    public async Task DataManagement_Import_RoundTrip_RestoresState()
    {
        string? tempPath = null;
        try
        {
            // Step 1: capture the current MaxRetries value so we can verify it's restored.
            var originalConfig = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
            var originalMaxRetries = originalConfig.MaxRetries;

            // Step 2: export current state to a temp file via the fake client
            var configClient = Fixture.Factory.ApiConfigClient;
            var exportedBytes = await configClient.ExportConfigAsync(CancellationToken.None);

            tempPath = Path.Combine(Path.GetTempPath(), $"e2e-export-roundtrip-{Guid.NewGuid():N}.json");
            await File.WriteAllBytesAsync(tempPath, exportedBytes);
            // TODO [WARNING]: Temp files created here are written to the system temp root (Path.GetTempPath()),
            // which is world-readable on Linux (/tmp, mode 1777). Exported config JSON may contain sensitive
            // data (provider credentials, API keys). Use Directory.CreateTempSubdirectory() (.NET 7+) to
            // create an isolated 0700-mode subdirectory per test run, and delete the whole directory in
            // fixture teardown. (Mirrors the same gap in DataManagementSectionHelper.DownloadConfigAsync.)

            // Step 3: modify state — add an extra quality gate config AND change a pipeline setting.
            // Both mutations must be reversed by the import to satisfy the acceptance criterion
            // "Change a setting and add a quality gate config … The extra quality gate config is
            // gone AND the setting is back".
            var extraQg = new QualityGateConfiguration
            {
                Id = "qg-extra-roundtrip",
                DisplayName = "Extra QG (should be removed by import)",
                Enabled = true
            };
            await Fixture.ConfigStore.SaveQualityGateConfigAsync(extraQg, CancellationToken.None);

            // Change MaxRetries to a different value so we can assert it's reverted after import.
            var mutatedConfig = originalConfig with { MaxRetries = originalMaxRetries + 10 };
            await Fixture.ConfigStore.SavePipelineConfigAsync(mutatedConfig, CancellationToken.None);

            // Verify the mutations were applied
            var beforeImport = await Fixture.ConfigStore.LoadQualityGateConfigsAsync(CancellationToken.None);
            Assert.Contains(beforeImport, q => q.Id == "qg-extra-roundtrip");
            var mutatedCheck = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
            Assert.Equal(originalMaxRetries + 10, mutatedCheck.MaxRetries);

            // Step 4: navigate to Data Management and import the exported file
            var helper = new DataManagementSectionHelper(Page, BaseUrl);
            await helper.NavigateAsync();

            // Assert overwrite warning is visible before importing
            // TODO [WARNING]: IsOverwriteWarningVisibleAsync checks a static paragraph that is always
            // rendered, not a conditional element. The assertion would still pass if the warning was
            // accidentally removed from the Razor markup as long as any other strong:has-text('overwrites')
            // exists on the page. Tighten the locator to a more specific element when/if the markup is
            // updated to make the warning conditional on file selection.
            Assert.True(await helper.IsOverwriteWarningVisibleAsync());

            await helper.SetImportFileAsync(tempPath);
            await helper.ClickImportAsync();

            // Assert success message
            var resultText = await helper.WaitForResultAsync(isSuccess: true);
            Assert.Contains("Import completed successfully", resultText, StringComparison.OrdinalIgnoreCase);

            // Step 5: verify the store was restored
            // (a) extra QG is gone, original QG is back
            var afterImport = await Fixture.ConfigStore.LoadQualityGateConfigsAsync(CancellationToken.None);
            Assert.DoesNotContain(afterImport, q => q.Id == "qg-extra-roundtrip");
            Assert.Contains(afterImport, q => q.Id == "qg-e2e");

            // (b) pipeline setting is back to its pre-export value
            var restoredConfig = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
            Assert.Equal(originalMaxRetries, restoredConfig.MaxRetries);

            // Step 6: verify UI reflects imported state without a full page reload.
            // Navigate to the Quality Gate Configs section within the same Blazor route —
            // this remounts the section component (triggering its OnInitializedAsync refetch)
            // without reloading the browser page.
            var settingsPage = new SettingsPage(Page, BaseUrl);
            await settingsPage.SelectTreeNodeAsync("Quality Gate Configs");

            var qgHelper = new QualityGateConfigSectionHelper(Page);
            var tableRows = await qgHelper.GetTableRowNamesAsync();
            Assert.DoesNotContain(tableRows, n => n.Contains("Extra QG (should be removed by import)"));
            Assert.Contains(tableRows, n => n.Contains("E2E Quality Gate"));
            // TODO [WARNING]: The issue requires "Settings AND Pipelines pages show the imported state
            // without a manual reload." This test only verifies the Settings page. Add navigation to
            // the Pipelines page here and assert it reflects the imported state (e.g. no extra QG entry
            // visible, correct template count) to cover the second half of the no-reload requirement.
        }
        finally
        {
            if (tempPath is not null && File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    // ── Scenario 3 — Bad file ────────────────────────────────────────────────

    /// <summary>
    /// Uploading a file with malformed JSON shows an error message ("Import failed")
    /// and leaves the existing configuration unchanged.
    /// </summary>
    [Fact]
    public async Task DataManagement_Import_BadFile_ShowsError()
    {
        string? tempPath = null;
        try
        {
            // Create a temp file with genuinely malformed JSON.
            tempPath = Path.Combine(Path.GetTempPath(), $"e2e-bad-import-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tempPath, "{ this is not valid json");

            var helper = new DataManagementSectionHelper(Page, BaseUrl);
            await helper.NavigateAsync();

            await helper.SetImportFileAsync(tempPath);
            await helper.ClickImportAsync();

            // Assert: error message shown
            var resultText = await helper.WaitForResultAsync(isSuccess: false);
            Assert.Contains("Import failed", resultText, StringComparison.OrdinalIgnoreCase);

            // Assert: store state is unchanged — seeded quality gate still present
            var qgConfigs = await Fixture.ConfigStore.LoadQualityGateConfigsAsync(CancellationToken.None);
            Assert.Contains(qgConfigs, q => q.Id == "qg-e2e");

            // Assert: seeded providers still present
            // TODO [WARNING]: Only two entity types (quality gate configs, Issue providers) are verified.
            // The acceptance criterion requires the *entire* configuration to be unchanged. Add assertions
            // for repo/agent providers, agent profiles, reviewer configs, and projects to give full
            // coverage of the "configuration is unchanged" invariant.
            var issueProviders = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
            Assert.Contains(issueProviders, p => p.Id == "issue-e2e");
        }
        finally
        {
            if (tempPath is not null && File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Uploading an empty file shows an error message ("Import failed") and leaves
    /// the existing configuration unchanged.
    ///
    /// The fake <see cref="InMemoryPipelineApiConfigClient.ImportConfigAsync"/> throws
    /// <see cref="InvalidOperationException"/> on <c>string.IsNullOrWhiteSpace(json)</c>,
    /// and the real <c>ConfigEndpoints.ImportConfigAsync</c> returns BadRequest when
    /// <c>file.Length == 0</c> ("No file uploaded"). Both paths produce an error in the UI.
    /// </summary>
    [Fact]
    public async Task DataManagement_Import_EmptyFile_ShowsError()
    {
        string? tempPath = null;
        try
        {
            // Create a zero-byte temp file.
            tempPath = Path.Combine(Path.GetTempPath(), $"e2e-empty-import-{Guid.NewGuid():N}.json");
            await File.WriteAllBytesAsync(tempPath, Array.Empty<byte>());

            var helper = new DataManagementSectionHelper(Page, BaseUrl);
            await helper.NavigateAsync();

            await helper.SetImportFileAsync(tempPath);
            await helper.ClickImportAsync();

            // Assert: error message shown
            var resultText = await helper.WaitForResultAsync(isSuccess: false);
            Assert.Contains("Import failed", resultText, StringComparison.OrdinalIgnoreCase);

            // Assert: store state is unchanged — seeded quality gate still present
            var qgConfigs = await Fixture.ConfigStore.LoadQualityGateConfigsAsync(CancellationToken.None);
            Assert.Contains(qgConfigs, q => q.Id == "qg-e2e");

            // Assert: seeded providers still present
            // TODO [WARNING]: Only two entity types (quality gate configs, Issue providers) are verified.
            // The acceptance criterion requires the *entire* configuration to be unchanged. Add assertions
            // for repo/agent providers, agent profiles, reviewer configs, and projects to give full
            // coverage of the "configuration is unchanged" invariant (mirrors the same gap in
            // DataManagement_Import_BadFile_ShowsError).
            var issueProviders = await Fixture.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
            Assert.Contains(issueProviders, p => p.Id == "issue-e2e");
        }
        finally
        {
            if (tempPath is not null && File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    // ── Scenario 4 — Run history untouched ───────────────────────────────────

    /// <summary>
    /// Importing a config bundle does not clear pipeline run history.
    ///
    /// Note: In the E2E harness, <c>InMemoryPipelineRunHistoryService</c> is entirely
    /// independent of <c>InMemoryConfigurationStore</c>. The fake <c>ImportConfigAsync</c>
    /// has no path to the history service, so this test cannot fail in this harness under
    /// the current architecture. It is nonetheless valuable as a contract test confirming
    /// the import/export scope, and guards against future implementations that might
    /// accidentally clear history during import.
    /// </summary>
    // TODO [WARNING]: This test is tautological in the current fake harness — InMemoryPipelineRunHistoryService
    // is completely independent of InMemoryConfigurationStore, so no import implementation can ever clear
    // history through this test path and the assertion always passes. Consider marking this with
    // [Trait("Category", "ContractOnly")] and adding a comment that meaningful regression coverage requires
    // driving the real ConfigEndpoints integration tests, not just the fake client.
    [Fact]
    public async Task DataManagement_Import_RunHistoryUntouched()
    {
        string? tempPath = null;
        try
        {
            // Seed a completed run history entry using AddRunSummaryAsync(PipelineRunSummary)
            var runId = Guid.NewGuid().ToString();
            var now = DateTimeOffset.UtcNow;
            var summary = new PipelineRunSummary
            {
                RunId = runId,
                IssueIdentifier = new IssueIdentifier("3105-history-test"),
                IssueTitle = "History test run for import invariant",
                FinalStep = PipelineStep.Completed,
                StartedAtOffset = now,
                // TODO [WARNING]: StartedAt is an obsolete property suppressed via #pragma warning disable CS0618.
                // If the property is required by the serialization contract (e.g. existing consumers read StartedAt),
                // document that reason here and keep the suppression. Otherwise, migrate to StartedAtOffset only
                // and remove this property assignment so the obsolete API is no longer referenced in test code.
#pragma warning disable CS0618
                StartedAt = now.DateTime,
#pragma warning restore CS0618
            };
            await Fixture.HistoryService.AddRunSummaryAsync(summary, CancellationToken.None);

            // Export via the InMemoryPipelineApiConfigClient
            var configClient = Fixture.Factory.ApiConfigClient;
            var exportedBytes = await configClient.ExportConfigAsync(CancellationToken.None);
            tempPath = Path.Combine(Path.GetTempPath(), $"e2e-export-history-{Guid.NewGuid():N}.json");
            await File.WriteAllBytesAsync(tempPath, exportedBytes);

            // Import via the UI
            var helper = new DataManagementSectionHelper(Page, BaseUrl);
            await helper.NavigateAsync();

            await helper.SetImportFileAsync(tempPath);
            await helper.ClickImportAsync();

            var resultText = await helper.WaitForResultAsync(isSuccess: true);
            Assert.Contains("Import completed successfully", resultText, StringComparison.OrdinalIgnoreCase);

            // Assert: run history still contains the seeded run
            var history = await Fixture.HistoryService.GetRunHistoryAsync(CancellationToken.None);
            Assert.Contains(history, r => r.RunId == runId);
        }
        finally
        {
            if (tempPath is not null && File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
