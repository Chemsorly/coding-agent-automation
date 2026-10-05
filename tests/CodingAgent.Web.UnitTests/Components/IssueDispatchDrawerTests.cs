using System.Reflection;
using System.Text.RegularExpressions;
using CodingAgent.Web.Components.Pages;

namespace CodingAgent.Web.UnitTests.Components;

public partial class IssueDispatchDrawerTests
{
    private static string InvokeGetBodyPreview(string body)
    {
        var method = typeof(IssueDispatchDrawer).GetMethod("GetBodyPreview", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [body])!;
    }

    [Fact]
    public void GetBodyPreview_ReturnsFirstContentLine()
    {
        var result = InvokeGetBodyPreview("This is the first line\nSecond line");
        Assert.Equal("This is the first line", result);
    }

    [Fact]
    public void GetBodyPreview_SkipsBlankLines()
    {
        var result = InvokeGetBodyPreview("\n\n  \nActual content");
        Assert.Equal("Actual content", result);
    }

    [Fact]
    public void GetBodyPreview_SkipsDoubleHashHeadings()
    {
        var result = InvokeGetBodyPreview("## Summary\nThe real content");
        Assert.Equal("The real content", result);
    }

    [Fact]
    public void GetBodyPreview_StripsLeadingHash()
    {
        var result = InvokeGetBodyPreview("# Title\nBody text");
        Assert.Equal("Title", result);
    }

    [Fact]
    public void GetBodyPreview_ReturnsEmpty_WhenAllLinesAreHeadingsOrBlank()
    {
        var result = InvokeGetBodyPreview("## Heading\n## Another\n\n");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetBodyPreview_ReturnsEmpty_ForEmptyBody()
    {
        var result = InvokeGetBodyPreview("");
        Assert.Equal(string.Empty, result);
    }

    // CSS regression guard: bUnit does not evaluate CSS. Blazor scopes *.razor.css to the
    // component it sits next to, so the badge rules must live in IssueDispatchDrawer.razor.css
    // (which renders the badges). In AgentCoding.razor.css they never matched, and a blocked
    // issue fell back to the green .badge-default.
    [Theory]
    [InlineData(".drawer-badge-blocked", "#C81E1E")]
    [InlineData(".drawer-badge-ready", "#137A3A")]
    public void DrawerBadgeRules_AreScopedToIssueDispatchDrawer(string selector, string background)
    {
        var drawerCss = File.ReadAllText(FindPagesFile("IssueDispatchDrawer.razor.css"));
        var rule = Regex.Match(drawerCss, Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(rule.Success, $"{selector} must be declared in IssueDispatchDrawer.razor.css");
        Assert.Contains($"background: {background};", rule.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color: #ffffff;", rule.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);

        var agentCodingCss = File.ReadAllText(FindPagesFile("AgentCoding.razor.css"));
        Assert.DoesNotContain(selector, agentCodingCss, StringComparison.Ordinal);
    }

    // Same scoping bug for the blocked row: the rule sat in AgentCoding.razor.css and never matched.
    // It must not set opacity on the row, because that also fades the Blocked badge inside it
    // below WCAG AA contrast.
    [Fact]
    public void DrawerIssueBlockedRule_IsScopedToIssueDispatchDrawer_AndDoesNotFadeTheRow()
    {
        var drawerCss = File.ReadAllText(FindPagesFile("IssueDispatchDrawer.razor.css"));
        var rule = DrawerIssueBlockedRulePattern().Match(drawerCss);
        Assert.True(rule.Success, ".drawer-issue-blocked must be declared in IssueDispatchDrawer.razor.css");
        Assert.Contains("border-color: var(--error);", rule.Groups["body"].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("opacity", rule.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);

        var agentCodingCss = File.ReadAllText(FindPagesFile("AgentCoding.razor.css"));
        Assert.DoesNotContain(".drawer-issue-blocked", agentCodingCss, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\.drawer-issue-blocked[^{]*\{(?<body>[^}]*)\}")]
    private static partial Regex DrawerIssueBlockedRulePattern();

    private static string FindPagesFile(string fileName)
    {
        var relative = Path.Combine("src", "CodingAgent.Web", "Components", "Pages", fileName);
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(IssueDispatchDrawerTests).Assembly.Location)!);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relative} above the test output directory.");
    }
}
