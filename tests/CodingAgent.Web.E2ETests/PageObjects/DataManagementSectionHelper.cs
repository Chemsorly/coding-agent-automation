using Microsoft.Playwright;

namespace CodingAgent.Web.E2ETests.PageObjects;

/// <summary>
/// Page helper for the Settings → Data Management section (ConfigImportExportSection.razor).
/// Covers export (download), import (file upload + button click), and result/error messages.
/// </summary>
public sealed class DataManagementSectionHelper
{
    private readonly IPage _page;

    public DataManagementSectionHelper(IPage page)
    {
        _page = page;
    }

    // ── Selectors ────────────────────────────────────────────────────────────────

    /// <summary>The "Download Config" button.</summary>
    private ILocator DownloadButton => _page.Locator("button:has-text('Download Config')");

    /// <summary>The hidden file input for selecting an import JSON file.</summary>
    private ILocator FileInput => _page.Locator("input[type='file'][accept='.json']");

    /// <summary>The "Import …" button (rendered once a file is selected).</summary>
    private ILocator ImportButton => _page.Locator("button:has-text('Import ')");

    /// <summary>Result message element rendered after export or import.</summary>
    private ILocator ResultMessage => _page.Locator("div.result-message");

    // ── Assertions ────────────────────────────────────────────────────────────────

    /// <summary>Waits for the Data Management section heading to be visible.</summary>
    public async Task WaitForSectionAsync()
    {
        await _page.WaitForSelectorAsync("section.settings-section:has(h2:has-text('Data Management'))",
            new() { Timeout = 10_000 });
    }

    /// <summary>Returns true if the overwrite warning text is visible in the import card.</summary>
    public async Task<bool> IsOverwriteWarningVisibleAsync()
    {
        // TODO [WARNING]: TextContentAsync() is called without first waiting for the card to be
        // attached to the DOM. Blazor Server renders asynchronously; if the import card has not
        // yet rendered after SelectImportFileAsync's 1s wait, this call will throw a
        // TimeoutException. Consider using WaitForAsync or a polling approach before reading text.
        var card = _page.Locator("div.action-card:has(h3:has-text('Import'))");
        var text = await card.TextContentAsync();
        return text?.Contains("overwrites", StringComparison.OrdinalIgnoreCase) == true
            || text?.Contains("permanently deleted", StringComparison.OrdinalIgnoreCase) == true;
    }

    // ── Export ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Clicks "Download Config" and waits for the browser download event.
    /// Returns the downloaded file content as a byte array and saves a copy to <paramref name="savePath"/>
    /// for assertions. The caller is responsible for deleting <paramref name="savePath"/> after the test.
    ///
    /// Requires the browser context to have been created with <c>AcceptDownloads = true</c>.
    /// </summary>
    public async Task<byte[]> ClickDownloadAndSaveAsync(string savePath)
    {
        var download = await _page.RunAndWaitForDownloadAsync(
            async () => await DownloadButton.ClickAsync(),
            new() { Timeout = 15_000 });

        await download.SaveAsAsync(savePath);

        var bytes = await File.ReadAllBytesAsync(savePath);
        return bytes;
    }

    /// <summary>Returns the suggested filename from the last download event.</summary>
    public static string GetSuggestedFileName(IDownload download) => download.SuggestedFilename;

    // ── Import ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the file input to <paramref name="filePath"/> (does NOT click Import).
    /// After this, <see cref="IsImportButtonVisibleAsync"/> should return true and
    /// <see cref="IsOverwriteWarningVisibleAsync"/> can be verified before clicking.
    /// </summary>
    public async Task SelectImportFileAsync(string filePath)
    {
        await FileInput.SetInputFilesAsync(filePath);
        // Wait for Blazor to process OnChange and render the Import button
        await _page.WaitForTimeoutAsync(1_000);
    }

    /// <summary>
    /// Returns true when the "Import …" button is visible (i.e., a file has been selected).
    /// </summary>
    public async Task<bool> IsImportButtonVisibleAsync()
        => await ImportButton.IsVisibleAsync();

    /// <summary>
    /// Clicks the "Import …" button and waits for the result message to appear.
    /// </summary>
    public async Task ClickImportAsync()
    {
        await ImportButton.ClickAsync();
        // Wait for async import to complete and result message to render
        await ResultMessage.WaitForAsync(new() { Timeout = 10_000 });
        // TODO [WARNING]: The unconditional 500ms sleep below adds latency to every scenario.
        // WaitForAsync already guarantees the element is in the DOM. If the Blazor circuit
        // disconnects during the async import call, div.result-message may never appear and the
        // WaitForAsync timeout above will produce a generic Playwright error instead of a
        // meaningful assertion failure. Consider replacing with a deterministic condition-based wait.
        await _page.WaitForTimeoutAsync(500);
    }

    // ── Result messages ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the text content of the result message (success or error) if visible.
    /// Returns null if no result message is shown.
    /// </summary>
    public async Task<string?> GetResultMessageAsync()
    {
        if (!await ResultMessage.IsVisibleAsync())
            return null;
        return await ResultMessage.TextContentAsync();
    }

    /// <summary>
    /// Returns true if a success result message is currently visible.
    /// </summary>
    public async Task<bool> IsSuccessMessageVisibleAsync()
    {
        var msg = _page.Locator("div.result-message.success");
        return await msg.IsVisibleAsync();
    }

    /// <summary>
    /// Returns true if an error result message is currently visible.
    /// </summary>
    public async Task<bool> IsErrorMessageVisibleAsync()
    {
        var msg = _page.Locator("div.result-message.error");
        return await msg.IsVisibleAsync();
    }
}
