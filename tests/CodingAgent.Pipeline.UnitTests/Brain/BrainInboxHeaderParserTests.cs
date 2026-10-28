using CodingAgent.Pipeline.Brain;
using AwesomeAssertions;

namespace CodingAgent.Pipeline.UnitTests.Brain;

public class BrainInboxHeaderParserTests
{
    private static readonly BrainInboxHeader SampleHeader = new(
        "run-1", "implementation", "succeeded",
        "owner/repo", "my-project", ["dotnet"],
        "#42", "https://github.com/o/r/pull/1",
        "abc123", ["repository", "general"]);

    [Fact]
    public void RoundTrip_ParsesComposedHeader()
    {
        var evidence = new BrainEvidence();
        var composed = BrainInboxComposer.Compose(SampleHeader, null, evidence, null, []);
        var ok = BrainInboxHeaderParser.TryParse(composed.Text, out var parsed, out var error);
        ok.Should().BeTrue(error);
        parsed.RunId.Should().Be("run-1");
        parsed.RepositoryFullName.Should().Be("owner/repo");
        parsed.Outcome.Should().Be("succeeded");
        parsed.Issue.Should().Be("#42");
        parsed.StackLabels.Should().BeEquivalentTo(["dotnet"]);
    }

    [Fact]
    public void TryParse_NoMarker_ReturnsFalse()
    {
        var ok = BrainInboxHeaderParser.TryParse("# Run foo\n- run type: x", out _, out var error);
        ok.Should().BeFalse();
        error.Should().Be("no marker");
    }

    [Fact]
    public void TryParse_MissingRepositoryLine_ReturnsFalse()
    {
        var text = $"{BrainLayout.InboxMarker}\n# Run foo\n- run type: implementation\n";
        var ok = BrainInboxHeaderParser.TryParse(text, out _, out var error);
        ok.Should().BeFalse();
        error.Should().Be("broken header");
    }

    [Fact]
    public void TryParse_50StackLabels_Handled()
    {
        var labels = string.Join(", ", Enumerable.Range(1, 50).Select(i => $"label{i}"));
        var text = $"{BrainLayout.InboxMarker}\n# Run foo\n- run type: x\n- outcome: succeeded\n- repository: o/r\n- stack labels: {labels}\n- issue: #1\n- skills injected from: none\n";
        var ok = BrainInboxHeaderParser.TryParse(text, out var parsed, out _);
        ok.Should().BeTrue();
        parsed.StackLabels.Should().HaveCount(50);
    }

    [Fact]
    public void TryParse_NoneStackLabels_ReturnsEmptyList()
    {
        var text = $"{BrainLayout.InboxMarker}\n# Run foo\n- run type: x\n- outcome: succeeded\n- repository: o/r\n- stack labels: none\n- issue: #1\n- skills injected from: none\n";
        var ok = BrainInboxHeaderParser.TryParse(text, out var parsed, out _);
        ok.Should().BeTrue();
        parsed.StackLabels.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_SkillsInjectedFromWithFiles_ParsesCommitAndFiles()
    {
        var text = $"{BrainLayout.InboxMarker}\n# Run foo\n- run type: x\n- outcome: succeeded\n- repository: o/r\n- stack labels: none\n- issue: #1\n- skills injected from: abc123 (repository, general)\n";
        var ok = BrainInboxHeaderParser.TryParse(text, out var parsed, out _);
        ok.Should().BeTrue();
        parsed.SkillsCommit.Should().Be("abc123");
        parsed.SkillsFiles.Should().BeEquivalentTo(["repository", "general"]);
    }
}
