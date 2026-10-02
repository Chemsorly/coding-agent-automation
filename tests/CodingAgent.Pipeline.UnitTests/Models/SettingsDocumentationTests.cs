using System.Text.RegularExpressions;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// The settings reference in docs/configuration.md lists every setting that is not internal, and nothing else: a setting
/// added without a row, or a row left behind for a removed setting, fails here.
/// </summary>
public class SettingsDocumentationTests
{
    /// <summary>Not operator settings; docs/configuration.md describes them under "Internal Fields" instead.</summary>
    private static readonly string[] InternalSettings =
    [
        nameof(PipelineConfiguration.ClosedLoopAutoStart),
        nameof(PipelineConfiguration.PipelineInjectedPaths),
        nameof(PipelineConfiguration.TransientRetryDelay),
        nameof(PipelineConfiguration.WorkspaceBaseDirectory),
    ];

    private static readonly Regex SettingRow = new(@"^\| `(?<name>[A-Za-z.]+)` \|", RegexOptions.Compiled | RegexOptions.Multiline, TimeSpan.FromSeconds(1));

    [Fact]
    public void SettingsReference_ListsExactlyTheSettings()
    {
        var reference = ReadSettingsReference();

        var documented = SettingRow.Matches(reference).Select(m => m.Groups["name"].Value).ToList();
        var expected = PipelineSettingsValidator.SettingPaths().Except(InternalSettings).Select(JsonName).ToList();

        documented.Should().OnlyHaveUniqueItems();
        documented.Should().BeEquivalentTo(expected,
            "docs/configuration.md must document every setting once, under the JSON name it is stored with");
    }

    [Fact]
    public void SettingsReference_MarksTheProjectOverridableSettings()
    {
        var reference = ReadSettingsReference();

        // Rows look like: | `name` | default | range | ✓ | description |
        var marked = reference.Split('\n')
            .Select(line => line.Split('|'))
            .Where(cells => cells.Length > 5 && SettingRow.IsMatch(string.Join('|', cells)) && cells[4].Trim() == "✓")
            .Select(cells => cells[1].Trim().Trim('`'))
            .ToList();

        marked.Should().BeEquivalentTo(PipelineSettingsValidator.ProjectOverridablePaths().Select(JsonName));
    }

    /// <summary>The stored JSON name of a setting path: camelCase segments, for example <c>codeReview.inlineComments.maxRetries</c>.</summary>
    private static string JsonName(string path) =>
        string.Join('.', path.Split('.').Select(segment => char.ToLowerInvariant(segment[0]) + segment[1..]));

    private static string ReadSettingsReference()
    {
        var docs = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "configuration.md")).Replace("\r\n", "\n");
        const string start = "<!-- settings-reference:start -->";
        const string end = "<!-- settings-reference:end -->";
        var from = docs.IndexOf(start, StringComparison.Ordinal);
        var to = docs.IndexOf(end, StringComparison.Ordinal);
        from.Should().BeGreaterThanOrEqualTo(0, "docs/configuration.md marks its settings reference");
        to.Should().BeGreaterThan(from);
        return docs[(from + start.Length)..to];
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Repository root not found.");
    }
}
