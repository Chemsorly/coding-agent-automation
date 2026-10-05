using System.Text.RegularExpressions;

namespace CodingAgent.Web.UnitTests.Components;

public class AgentCodingScopedCssTests
{
    // CSS regression guard: bUnit does not evaluate CSS. AgentCoding.razor.css is CSS-isolated, so
    // its rules only match elements authored in AgentCoding.razor. Rules for markup rendered by
    // child components (the dispatch drawers' rows, toggle switches, ...) silently never apply and
    // belong in that component's own .razor.css. Classes after ::deep are exempt, since ::deep
    // exists to reach child components.
    [Fact]
    public void ScopedCss_OnlyTargetsClassesAuthoredInAgentCodingMarkup()
    {
        var css = Regex.Replace(File.ReadAllText(FindPagesFile("AgentCoding.razor.css")), @"/\*.*?\*/", "", RegexOptions.Singleline);
        var markup = File.ReadAllText(FindPagesFile("AgentCoding.razor"));

        var classes = Regex.Matches(css, @"(?<selector>[^{}]+)\{")
            .Select(m => m.Groups["selector"].Value)
            .Where(selector => !selector.TrimStart().StartsWith('@'))
            .SelectMany(selector => selector.Split(','))
            .Select(selector => selector.Split("::deep")[0])
            .SelectMany(selector => Regex.Matches(selector, @"\.(?<name>-?[A-Za-z_][\w-]*)").Select(m => m.Groups["name"].Value))
            .Distinct()
            .ToList();
        Assert.NotEmpty(classes);

        var notInMarkup = classes
            .Where(name => !Regex.IsMatch(markup, $@"(?<![\w-]){Regex.Escape(name)}(?![\w-])"))
            .ToList();
        Assert.True(notInMarkup.Count == 0,
            $"AgentCoding.razor.css styles classes that AgentCoding.razor does not render: {string.Join(", ", notInMarkup)}. " +
            "Move each rule to the .razor.css of the component that renders the element, or delete it.");
    }

    private static string FindPagesFile(string fileName)
    {
        var relative = Path.Combine("src", "CodingAgent.Web", "Components", "Pages", fileName);
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(AgentCodingScopedCssTests).Assembly.Location)!);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relative} above the test output directory.");
    }
}
