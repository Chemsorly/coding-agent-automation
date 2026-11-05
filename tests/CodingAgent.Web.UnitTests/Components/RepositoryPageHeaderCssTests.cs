using System.Text.RegularExpressions;

namespace CodingAgent.Web.UnitTests.Components;

public partial class RepositoryPageHeaderCssTests
{
    private sealed record MediaBlock(int MaxWidthPx, int Start, string Body);

    [GeneratedRegex(@"@media\s*\(\s*max-width:\s*(?<px>\d+)px\s*\)\s*\{")]
    private static partial Regex MaxWidthMediaStart();

    [GeneratedRegex(@"(?m)^\.cockpit-page-actions\s*\{[^}]*max-width:\s*60%")]
    private static partial Regex ActionsWidthCap();

    [GeneratedRegex(@"(?m)^\s*\.cockpit-page-actions\s*\{(?=[^}]*max-width:\s*100%)(?=[^}]*justify-content:\s*flex-start)[^}]*\}")]
    private static partial Regex ActionsWidthCapOverride();

    [GeneratedRegex(@"(?m)^\s*\.cockpit-page-header\s*\{[^}]*flex-wrap:\s*wrap")]
    private static partial Regex HeaderWrapRule();

    // CSS regression guard: bUnit does not evaluate CSS, and RepositoryPage.razor used these classes
    // with no rule anywhere, so its header collapsed (issue #4210). A rule must also keep the
    // declarations that stop the collapse, not just exist.
    [Theory]
    [InlineData("cockpit-breadcrumb", "display: flex; flex-wrap: wrap; gap: 6px")]
    [InlineData("cockpit-breadcrumb-sep", "color: var(--text-faint)")]
    [InlineData("cockpit-page-chips", "display: flex; flex-wrap: wrap; gap: 8px")]
    [InlineData("badge-project", "font-size: 12.5px; color: var(--text-muted)")]
    [InlineData("cockpit-page-actions", "display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 8px; max-width: 60%")]
    [InlineData("settings-readonly-row", "display: flex; align-items: center; gap: 8px")]
    public void CockpitCss_StylesTheRepositoryPageHeaderClass(string className, string declarations)
    {
        var css = File.ReadAllText(FindCockpitCss());

        var rule = Regex.Match(css, $@"(?m)^\.{Regex.Escape(className)}\s*\{{(?<body>[^}}]*)\}}");
        Assert.True(rule.Success, $"cockpit.css has no top-level .{className} rule.");

        var body = WithoutWhitespace(rule.Groups["body"].Value);
        foreach (var declaration in declarations.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.True(body.Contains(WithoutWhitespace(declaration), StringComparison.Ordinal),
                $".{className} lacks '{declaration}'.");
        }
    }

    // The issue requires the header actions to drop their 60% cap at or below 768px. Both rules have the
    // same specificity, so the override only wins when it comes after the base rule.
    [Fact]
    public void CockpitCss_ActionsLoseTheirWidthCapAtOrBelow768px()
    {
        var css = File.ReadAllText(FindCockpitCss());

        var cap = ActionsWidthCap().Match(css);
        Assert.True(cap.Success, "cockpit.css has no .cockpit-page-actions rule with max-width: 60%.");

        Assert.True(
            MaxWidthMediaBlocks(css).Any(m => m.MaxWidthPx == 768 && m.Start > cap.Index && ActionsWidthCapOverride().IsMatch(m.Body)),
            "No @media (max-width: 768px) block after the base rule overrides .cockpit-page-actions with max-width: 100% and justify-content: flex-start.");
    }

    // The issue also places the override in the block that holds the header's wrap rule: that is where the
    // actions sit on their own row and a 60% strip would look broken.
    [Fact]
    public void CockpitCss_ActionsLoseTheirWidthCapInTheBlockWhereTheHeaderWraps()
    {
        var css = File.ReadAllText(FindCockpitCss());

        var wrapping = MaxWidthMediaBlocks(css).Where(m => HeaderWrapRule().IsMatch(m.Body)).ToList();

        Assert.NotEmpty(wrapping);
        Assert.All(wrapping, m => Assert.Matches(ActionsWidthCapOverride(), m.Body));
    }

    private static string WithoutWhitespace(string text) => Regex.Replace(text, @"\s+", "");

    private static IEnumerable<MediaBlock> MaxWidthMediaBlocks(string css)
    {
        foreach (Match start in MaxWidthMediaStart().Matches(css))
        {
            var bodyStart = start.Index + start.Length;
            var depth = 1;
            var i = bodyStart;
            for (; i < css.Length && depth > 0; i++)
            {
                if (css[i] == '{') depth++;
                else if (css[i] == '}') depth--;
            }
            if (depth != 0) throw new InvalidOperationException("Unbalanced braces in cockpit.css.");

            yield return new MediaBlock(int.Parse(start.Groups["px"].Value), start.Index, css[bodyStart..(i - 1)]);
        }
    }

    private static string FindCockpitCss()
    {
        var relative = Path.Combine("src", "CodingAgent.Web", "wwwroot", "css", "cockpit.css");
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(RepositoryPageHeaderCssTests).Assembly.Location)!);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relative} above the test output directory.");
    }
}
