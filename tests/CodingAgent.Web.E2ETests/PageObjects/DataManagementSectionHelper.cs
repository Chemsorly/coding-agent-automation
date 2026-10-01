using CodingAgent.Pipeline.Models;
using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page object for the Data Management → Import / Export section of the Settings page.
///
/// CSS selector notes derived from <c>ConfigImportExportSection.razor</c>:
/// - Download button: <c>button:has-text('Download Config')</c> — inside <c>div.action-card</c>
/// - File input: <c>input[accept='.json']</c> — rendered by Blazor's <c>&lt;InputFile&gt;</c>
/// - Import button: <c>button.btn-primary:has-text('Import ')</c> — only rendered when a file is selected
/// - Result message: <c>div.result-message.success</c> or <c>div.result-message.error</c>
/// - The Data Management group in SettingsTreeNav is initialized expanded (_dataManagementExpanded = true),
///   so the "Import / Export" tree node is always visible without needing to expand the group.
/// </summary>
public sealed class DataManagementSectionHelper
{
    private readonly IPage _page;
    private readonly string _baseUrl;

    // The section query parameter value for the Import / Export node (SettingsNodes.ConfigImportExport)
    private const string SectionParam = "config-import-export";

    public DataManagementSectionHelper(IPage page, string baseUrl)
    {
        _page = page;
        _baseUrl = baseUrl;
    }

    // ── Locators ────────────────────────────────────────────────────────────

    private ILocator DownloadButton => _page.Locator("button:has-text('Download Config')");
    private ILocator FileInput => _page.Locator("input[accept='.json']");

    // The import button text is "Import <filename>" — contains 'Import ' prefix
    private ILocator ImportButton => _page.Locator("button.btn-primary:has-text('Import ')");

    private ILocator SuccessMessage => _page.Locator("div.result-message.success");
    private ILocator ErrorMessage => _page.Locator("div.result-message.error");

    // ── Navigation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Navigates directly to the Import / Export settings section by URL, bypassing
    /// the need to expand tree groups or click tree nodes.
    /// </summary>
    public async Task NavigateAsync()
    {
        await _page.GotoAsync($"{_baseUrl}/settings?section={SectionParam}");
        await _page.WaitForSelectorAsync("h1", new() { Timeout = 15_000 });
        // TODO [WARNING]: This fixed sleep is a reliability risk under CI load; the Blazor circuit
        // may take longer than 3 s to connect and mount the section component. Replace with a
        // deterministic wait (e.g. wait for a stable DOM element rendered only after the circuit
        // is fully connected) to eliminate the flake risk.
        // Allow Blazor Server circuit to connect and the section component to initialize
        await _page.WaitForTimeoutAsync(3_000);
        // Confirm we're on the right section by waiting for the Download Config button
        await DownloadButton.WaitForAsync(new() { Timeout = 5_000 });
    }

    // ── Export ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Clicks the "Download Config" button without attempting to capture the download.
    /// Blob URL downloads (created by URL.createObjectURL in downloadFileFromStream) are not
    /// reliably interceptable via Playwright's download event in headless Chromium, so this
    /// method simply triggers the button click. Use <see cref="WaitForResultAsync"/> afterwards
    /// to confirm the export completed successfully.
    /// </summary>
    public async Task ClickExportAsync()
    {
        await DownloadButton.ClickAsync();
    }

    /// <summary>
    /// Clicks "Download Config" and captures the Playwright download event.
    /// Saves the file to a unique path under <see cref="Path.GetTempPath()"/>.
    ///
    /// NOTE: This method is unreliable in headless Chromium for Blob URL downloads.
    /// The <c>downloadFileFromStream</c> JS function uses <c>URL.createObjectURL</c> which
    /// produces a Blob URL that Playwright cannot intercept as a download event in all
    /// environments. Prefer <see cref="ClickExportAsync"/> for UI interaction tests and
    /// verify bundle content directly via the fake client.
    /// </summary>
    /// <returns>
    /// The temp file path the download was saved to. The caller is responsible for
    /// deleting the file (use a <c>try/finally</c> block).
    /// </returns>
    public async Task<string> DownloadConfigAsync()
    {
        // TODO [WARNING]: Files are saved directly to Path.GetTempPath() (the system temp root).
        // On multi-user systems this directory is shared and world-readable. Consider writing to a
        // test-scoped subdirectory (e.g. Path.Combine(Path.GetTempPath(), "e2e-" + Guid.NewGuid()))
        // created by the fixture and deleted in its teardown, so all downloads are isolated per run.
        var tempPath = Path.Combine(Path.GetTempPath(), $"e2e-export-{Guid.NewGuid():N}.json");

        var download = await _page.RunAndWaitForDownloadAsync(async () =>
        {
            await DownloadButton.ClickAsync();
        });

        await download.SaveAsAsync(tempPath);
        return tempPath;
    }

    // ── Import ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the file input to the given path using Playwright's SetInputFilesAsync,
    /// then waits for Blazor to re-render the conditional "Import &lt;filename&gt;" button.
    /// </summary>
    public async Task SetImportFileAsync(string filePath)
    {
        await FileInput.SetInputFilesAsync(filePath);
        // TODO [WARNING]: This fixed sleep is a reliability risk under CI load; the Blazor
        // InputFile OnChange round-trip may take longer than 500 ms. Replace with a
        // deterministic wait on the ImportButton's visibility directly, removing the sleep.
        // Allow Blazor Server to process the InputFile OnChange event and re-render
        await _page.WaitForTimeoutAsync(500);
        // Confirm the import button appeared
        await ImportButton.WaitForAsync(new() { Timeout = 5_000 });
    }

    /// <summary>Clicks the "Import &lt;filename&gt;" button.</summary>
    public async Task ClickImportAsync()
    {
        await ImportButton.ClickAsync();
    }

    // ── Assertions ──────────────────────────────────────────────────────────

    /// <summary>
    /// Waits for the result message element to appear and returns its text content.
    /// </summary>
    /// <param name="isSuccess">
    /// <c>true</c> waits for <c>div.result-message.success</c>;
    /// <c>false</c> waits for <c>div.result-message.error</c>.
    /// </param>
    public async Task<string> WaitForResultAsync(bool isSuccess, int timeoutMs = 10_000)
    {
        var locator = isSuccess ? SuccessMessage : ErrorMessage;
        await locator.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = timeoutMs });
        return (await locator.TextContentAsync())?.Trim() ?? "";
    }

    /// <summary>
    /// Returns true if the overwrite warning paragraph is visible.
    /// The warning is always rendered (not conditional on file selection); it is the
    /// static paragraph inside the Import Configuration action card that contains
    /// the word "overwrites".
    /// </summary>
    public async Task<bool> IsOverwriteWarningVisibleAsync()
    {
        var warning = _page.Locator("div.action-card p strong:has-text('overwrites')");
        return await warning.IsVisibleAsync();
    }
}
