using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// CSS regression guard for issue #4205. bUnit does not evaluate CSS, so these tests read
/// <c>cockpit.css</c> and pin the rules that keep the Work and Runs rows identifiable on tablet widths
/// and keep the top bar and page header inside the viewport on a phone.
/// </summary>
public partial class CockpitLayoutResponsiveCssTests
{
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssCommentPattern();

    [GeneratedRegex(@"@media\s*(?<condition>\([^)]*\))\s*\{")]
    private static partial Regex MediaOpenPattern();

    [GeneratedRegex(@"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex FlatRulePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    private sealed record Rule(IReadOnlyList<string> Selectors, IDictionary<string, string> Declarations);

    private sealed record MediaBlock(string Condition, string Body);

    private static readonly Lazy<string> Css = new(() =>
        CssCommentPattern().Replace(File.ReadAllText(FindSourceFile("CodingAgent.Web", "wwwroot", "css", "cockpit.css")), ""));

    [Fact]
    public void RunsTitleCell_KeepsTheDesktopEllipsisRule()
    {
        var rule = FindRule(Rules(WithoutMediaBlocks(Css.Value)), ".cockpit .runs-title-cell");

        rule.Declarations.Should().Contain("overflow", "hidden");
        rule.Declarations.Should().Contain("text-overflow", "ellipsis");
        rule.Declarations.Should().Contain("max-width", "0",
            "above 1024px the Run column takes the spare width and truncates, as the inline style did");
    }

    [Fact]
    public void WorkIssueColumn_KeepsTheDesktopSpareWidthRule()
    {
        var rule = FindRule(Rules(WithoutMediaBlocks(Css.Value)), ".cockpit .work-table td:first-child");

        rule.Declarations.Should().Contain("width", "100%",
            "above 1024px the Issue column takes the spare width so Cancel/Remove stay in view at 1440px");
        rule.Declarations.Should().Contain("max-width", "0");
    }

    [Fact]
    public void TabletBlock_LetsTheIdentityColumnsKeepARealWidthAndWrap()
    {
        var block = MediaBlocks(Css.Value).Single(b =>
            b.Condition == "(max-width: 1024px)" && b.Body.Contains(".cockpit .runs-title-cell"));

        var rule = FindRule(Rules(block.Body), ".cockpit .runs-title-cell");

        rule.Selectors.Should().Contain(".cockpit .work-table td:first-child",
            "the Work issue column collapses the same way as the Runs title column");
        rule.Declarations.Should().Contain("width", "auto");
        rule.Declarations.Should().Contain("max-width", "none");
        rule.Declarations.Should().Contain("min-width", "240px");
        rule.Declarations.Should().Contain("white-space", "normal");
    }

    [Fact]
    public void TabletBlock_FollowsTheDesktopRules_SoItWinsAtEqualSpecificity()
    {
        var css = Css.Value;
        var tablet = MediaOpenPattern().Matches(css)
            .Single(m => m.Groups["condition"].Value == "(max-width: 1024px)" && ClosingBodyOf(css, m).Contains(".cockpit .runs-title-cell"));

        DesktopRule(@"\.cockpit \.work-table td:first-child\s*\{").Index.Should().BeLessThan(tablet.Index,
            "the media block repeats the selector of the base rule and relies on coming after it");
        DesktopRule(@"\.cockpit \.runs-title-cell\s*\{").Index.Should().BeLessThan(tablet.Index,
            "the media block repeats the selector of the base rule and relies on coming after it");

        Match DesktopRule(string pattern)
        {
            var match = Regex.Match(css, pattern);
            match.Success.Should().BeTrue($"the base rule '{pattern}' must exist in cockpit.css for the media block to override");
            return match;
        }
    }

    [Fact]
    public void PhoneBlock_WrapsThePageHeaderActions()
    {
        var rule = FindRule(Rules(PhoneBlock().Body), ".cockpit-page-header > :last-child");

        rule.Declarations.Should().Contain("flex-wrap", "wrap");
        rule.Declarations.Should().Contain("min-width", "0");
        rule.Declarations.Should().Contain("max-width", "100%");
    }

    [Fact]
    public void PhoneBlock_KeepsTheAttentionBadgeOnOneLine()
    {
        var rule = FindRule(Rules(PhoneBlock().Body), ".cockpit-attention");

        rule.Declarations.Should().Contain("white-space", "nowrap");
    }

    [Fact]
    public void PhoneBlock_HidesTheBadgeWordsAndTheUserName_WithCssOnly()
    {
        var rule = FindRule(Rules(PhoneBlock().Body), ".cockpit-attention-label");

        rule.Selectors.Should().Contain(".cockpit-user-name");
        rule.Declarations.Should().Contain("display", "none");
    }

    [Fact]
    public void PhoneBlock_LetsTheProjectSwitcherGrowIntoTheFreedSpace()
    {
        var rule = FindRule(Rules(PhoneBlock().Body), ".cockpit-project-switcher");

        rule.Declarations.Should().Contain("flex", "1 1 auto");
    }

    // The sidebar drawer rules identify the page-chrome block; a second ≤640px block holds the triage rules.
    private static MediaBlock PhoneBlock() =>
        MediaBlocks(Css.Value).Single(b => b.Condition == "(max-width: 640px)" && b.Body.Contains(".cockpit-sidebar.is-open"));

    private static Rule FindRule(IEnumerable<Rule> rules, string selector)
    {
        var matches = rules.Where(r => r.Selectors.Contains(selector)).ToList();
        matches.Should().NotBeEmpty($"a rule for '{selector}' must exist");
        return matches[^1];
    }

    private static IReadOnlyList<Rule> Rules(string css) =>
        FlatRulePattern().Matches(css)
            .Select(m => new Rule(
                m.Groups["selectors"].Value.Split(',').Select(Normalize).Where(s => s.Length > 0).ToList(),
                m.Groups["body"].Value.Split(';')
                    .Select(d => d.Split(':', 2))
                    .Where(parts => parts.Length == 2)
                    .GroupBy(parts => parts[0].Trim())
                    .ToDictionary(g => g.Key, g => Normalize(g.Last()[1]))))
            .ToList();

    private static string Normalize(string text) => WhitespacePattern().Replace(text.Trim(), " ");

    private static IReadOnlyList<MediaBlock> MediaBlocks(string css)
    {
        var blocks = new List<MediaBlock>();
        foreach (Match open in MediaOpenPattern().Matches(css))
        {
            blocks.Add(new MediaBlock(Normalize(open.Groups["condition"].Value), ClosingBodyOf(css, open)));
        }
        return blocks;
    }

    private static string ClosingBodyOf(string css, Match open)
    {
        var start = open.Index + open.Length;
        return css[start..ClosingBrace(css, start)];
    }

    private static string WithoutMediaBlocks(string css)
    {
        var result = new System.Text.StringBuilder();
        var position = 0;
        foreach (Match open in MediaOpenPattern().Matches(css))
        {
            if (open.Index < position) continue;
            result.Append(css, position, open.Index - position);
            position = ClosingBrace(css, open.Index + open.Length) + 1;
        }
        result.Append(css, position, css.Length - position);
        return result.ToString();
    }

    /// <summary>Index of the brace that closes the block whose opening brace precedes <paramref name="start"/>.</summary>
    private static int ClosingBrace(string css, int start)
    {
        var depth = 1;
        for (var i = start; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) return i;
        }
        throw new InvalidOperationException("cockpit.css has an unbalanced @media block.");
    }

    private static string FindSourceFile(params string[] segments)
    {
        var relative = Path.Combine(["src", .. segments]);
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(CockpitLayoutResponsiveCssTests).Assembly.Location)!);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relative} above the test output directory.");
    }
}
