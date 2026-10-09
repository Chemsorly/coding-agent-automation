namespace CodingAgent.Pipeline.UnitTests.Architecture;

/// <summary>
/// Source-scan guards for E2E navigation patterns.
///
/// Cockpit pages are prerendered by Blazor Server. Until the circuit has rendered
/// <c>CockpitLayout</c>, event handlers are absent and reads may see prerendered
/// or "Loading…" markup. Every cockpit page load must go through
/// <c>GotoCockpitPageAsync</c> or <c>ReloadCockpitPageAsync</c> from
/// <c>BlazorPageExtensions</c>, not a raw <c>GotoAsync</c>/<c>ReloadAsync</c>.
///
/// These tests run in the regular unit-test job (no browser required).
/// </summary>
public partial class LayerBoundaryTests
{
    [Fact]
    public void E2E_BrowserNavigations_UseCockpitReadyHelpers()
    {
        var e2eDir = Path.Combine(RepoRoot, "tests", "CodingAgent.Web.E2ETests");

        // AccessControlTests drives the login form, sign-in redirects and access-denied pages
        // (no cockpit shell) and asserts only through retrying Assertions.Expect — exempt.
        var exemptFiles = new[] { "AccessControlTests.cs" };
        var files = new[] { "PageObjects", "Tests" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(e2eDir, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !exemptFiles.Contains(Path.GetFileName(f)));

        var offenders = new List<string>();
        var helperCalls = 0;
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal))
                    continue;
                // TODO [WARNING]: This scan skips single-line // comments (after Trim) but does NOT
                // skip block comments (/* … */) or multi-line verbatim string literals. A GotoAsync
                // example inside a /* */ comment, or a raw .GotoAsync( inside a string literal, would
                // be flagged as a false-positive offender. In practice no such patterns exist today.
                // If they are introduced, extend the scanner to track block-comment state across lines.
                // (Correctness finding, LayerBoundaryTests.E2ENavigation.cs:38)
                if (line.Contains("GotoCockpitPageAsync(", StringComparison.Ordinal)
                    || line.Contains("ReloadCockpitPageAsync(", StringComparison.Ordinal))
                    helperCalls++;
                var raw = line.Contains(".GotoAsync(", StringComparison.Ordinal)
                    || line.Contains(".ReloadAsync(", StringComparison.Ordinal);
                if (raw && !line.Contains("\"about:blank\"", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}:{i + 1}: {line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Cockpit pages are prerendered; a raw GotoAsync/ReloadAsync lets the test act before the Blazor circuit is " +
            "interactive. Use Page.GotoCockpitPageAsync(url) / Page.ReloadCockpitPageAsync() from BlazorPageExtensions:\n" +
            string.Join("\n", offenders));
        Assert.True(helperCalls > 0,
            "Positive control: no GotoCockpitPageAsync/ReloadCockpitPageAsync call found — the scan is not seeing the E2E sources.");
    }

    [Fact]
    public void E2E_WaitForBlazorAsync_IsNotReintroduced()
    {
        var e2eDir = Path.Combine(RepoRoot, "tests", "CodingAgent.Web.E2ETests");
        var sep = Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(e2eDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
            .Where(f => File.ReadAllText(f).Contains("WaitForBlazorAsync", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "WaitForBlazorAsync only proved that blazor.web.js loaded, not that the circuit was interactive. " +
            "Use GotoCockpitPageAsync / WaitForCockpitPageReadyAsync / WaitForInteractiveAsync instead: " +
            string.Join(", ", offenders));
    }
}
