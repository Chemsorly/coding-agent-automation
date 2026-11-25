using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// CSS regression guard for issue #4211. bUnit does not evaluate CSS, so these tests read <c>cockpit.css</c> and pin
/// the declarations that keep the routing editor (Settings → Routing and the repository Binding card) readable:
/// the profile name ends before the select's arrow, the inherited route is not italic, and the table keeps a width
/// below which it scrolls in its wrapper. They pin the declarations, not what a browser does with them.
/// </summary>
public partial class RouteTableCssTests
{
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssCommentPattern();

    [GeneratedRegex(@"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex FlatRulePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"\.[A-Za-z][\w-]*")]
    private static partial Regex ClassPattern();

    [GeneratedRegex(@"\[[^\]]+\]")]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@":(?!not\()[A-Za-z][\w-]*")]
    private static partial Regex PseudoClassPattern();

    private static readonly Lazy<string> RawCss = new(() => File.ReadAllText(FindSourceFile("CodingAgent.Web", "wwwroot", "css", "cockpit.css")));

    private static readonly Lazy<IReadOnlyList<(IReadOnlyList<string> Selectors, IReadOnlyList<(string Name, string Value)> Declarations)>> Rules = new(() =>
        FlatRulePattern().Matches(CssCommentPattern().Replace(RawCss.Value, ""))
            .Select(m => (
                (IReadOnlyList<string>)m.Groups["selectors"].Value.Split(',').Select(Normalize).Where(s => s.Length > 0).ToList(),
                (IReadOnlyList<(string, string)>)m.Groups["body"].Value.Split(';')
                    .Select(d => d.Split(':', 2))
                    .Where(parts => parts.Length == 2)
                    .Select(parts => (parts[0].Trim(), Normalize(parts[1])))
                    .ToList()))
            .ToList());

    [Theory]
    [InlineData(1, "14%")]
    [InlineData(2, "37%")]
    [InlineData(3, "20%")]
    [InlineData(4, "13%")]
    [InlineData(5, "16%")]
    public void TheColumns_HaveTheWidthsThatLetTheProfileColumnTakeTheSpare(int column, string width)
    {
        Declarations($".cockpit .route-table th:nth-child({column})").Should().Contain("width", width,
            "the profile column (37%) holds 'Claude Dotnet 10 Agent (Claude Code)' and the first column no longer needs 15%");
    }

    [Fact]
    public void TheColumnWidths_AddUpToTheWholeTable()
    {
        var total = Enumerable.Range(1, 5)
            .Sum(column => int.Parse(Declarations($".cockpit .route-table th:nth-child({column})")["width"].TrimEnd('%')));

        total.Should().Be(100, "the fixed table layout gives the columns exactly the table's width");
    }

    [Fact]
    public void TheSelects_KeepTheirTextClearOfTheArrow_AndEndLongNamesInAnEllipsis()
    {
        var declarations = Declarations(".cockpit .route-table select");

        declarations.Should().Contain("padding-right", "28px", "the arrow sits over the select's right padding");
        declarations.Should().Contain("text-overflow", "ellipsis");
    }

    [Fact]
    public void TheTable_HasAWidthBelowWhichItScrollsInsteadOfSplittingWords()
    {
        var declarations = Declarations(".cockpit .route-table");

        declarations.Should().Contain("min-width", "820px");
        declarations.Should().Contain("table-layout", "fixed", "the percentage columns need the fixed layout");
    }

    private const string FirstColumnSelector = ".cockpit .monitoring-table.route-table td:first-child:not(:last-child)";

    [Fact]
    public void TheFirstColumn_DeclaresNormalOverflowWrap()
    {
        Declarations(FirstColumnSelector).Should().Contain("overflow-wrap", "normal");
    }

    // The section list rules set "anywhere" / "break-word" on the same cell and sit later in the file, so the
    // first-column rule only keeps whole words if its specificity is higher than theirs.
    [Fact]
    public void TheFirstColumnRule_OutranksTheSectionListRulesThatAllowBreaksInsideWords()
    {
        var competing = Rules.Value
            .Where(rule => rule.Declarations.Any(d => d.Name == "overflow-wrap"))
            .SelectMany(rule => rule.Selectors)
            .Where(selector => selector.EndsWith(".monitoring-table td:not(:last-child)", StringComparison.Ordinal))
            .ToList();

        competing.Should().NotBeEmpty("the section list rules are what this rule has to outrank");
        competing.Should().OnlyContain(selector => Specificity(FirstColumnSelector) > Specificity(selector),
            "a rule that does not outrank them never applies in a .settings-section");
    }

    [Fact]
    public void TheSaveError_UnderTheLastField_KeepsTheGapATableGaveIt_AndSaveKeepsItsGap()
    {
        Declarations(".cockpit .settings-section > .form-group + .settings-status:not(.settings-status-toast)")
            .Should().Contain("margin-top", "12px", "the banner has no margin of its own and now follows the timeout field");
        Declarations(".cockpit .settings-section > .form-group + .settings-status + .form-buttons")
            .Should().Contain("margin-top", "16px", "'.form-group + .form-buttons' no longer matches once the banner sits between them");
    }

    [Fact]
    public void TheInheritedRoute_IsNotItalicAndWraps()
    {
        var declarations = Declarations(".cockpit .route-table td[data-testid^=\"route-inherit-\"]");

        declarations.Should().Contain("font-style", "normal", "the inherited route is information; .monitoring-muted makes it italic");
        declarations.Should().Contain("white-space", "normal", "the text spans three columns and has to wrap");
    }

    [Fact]
    public void TheInheritedRouteRule_IsOneLine()
    {
        var lines = RawCss.Value.Split('\n').Count(line => line.Contains("route-table td[data-testid", StringComparison.Ordinal));

        lines.Should().Be(1);
    }

    // Every declaration of every rule whose selector list names this selector (".cockpit .route-table" has two rules).
    private static Dictionary<string, string> Declarations(string selector)
    {
        var wanted = Normalize(selector);
        var declarations = Rules.Value
            .Where(rule => rule.Selectors.Contains(wanted))
            .SelectMany(rule => rule.Declarations)
            .GroupBy(d => d.Name)
            .ToDictionary(g => g.Key, g => g.Last().Value);
        declarations.Should().NotBeEmpty($"a rule for '{wanted}' must exist in cockpit.css");
        return declarations;
    }

    // Classes, attributes and pseudo-classes in a selector (:not() counts its argument, not itself). The compared
    // selectors all end in "td", so the element count is the same.
    private static int Specificity(string selector) =>
        ClassPattern().Matches(selector).Count + AttributePattern().Matches(selector).Count + PseudoClassPattern().Matches(selector).Count;

    private static string Normalize(string text) => WhitespacePattern().Replace(text.Trim(), " ");

    private static string FindSourceFile(params string[] segments)
    {
        var relative = Path.Combine(["src", .. segments]);
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(RouteTableCssTests).Assembly.Location)!);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relative} above the test output directory.");
    }
}
