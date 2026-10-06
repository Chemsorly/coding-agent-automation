using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="ReworkContextWriter.Format"/>: the <c>.agent/rework-context.md</c> file that
/// tells a rework agent what the rebase onto main dropped from its branch (issue #3093).
/// </summary>
public class ReworkContextWriterTests
{
    private const string MergeBase = "aaaaaaaa11111111";
    private const string PreviousHead = "bbbbbbbb22222222";
    private const string BaseHead = "cccccccc33333333";

    private static readonly ForceResolvedFileContext ClassifierContext = new()
    {
        Path = "src/CiFailureClassifier.cs",
        BranchChange = "@@ -1 +1 @@\n-old\n+branch fix\n",
        BaseChange = "@@ -1 +1 @@\n-old\n+main fix\n",
        BaseCommits = ["1234abcd fix: Give agents the log of a timed-out CI job"]
    };

    [Fact]
    public void Format_ListsEachFileWithDroppedChangeMainsChangeAndCommits()
    {
        var content = ReworkContextWriter.Format(ForceResolvedResult([ClassifierContext]));

        content.Should().Contain("`src/CiFailureClassifier.cs`");
        content.Should().Contain("+branch fix");
        content.Should().Contain("+main fix");
        content.Should().Contain("1234abcd fix: Give agents the log of a timed-out CI job");
    }

    [Fact]
    public void Format_StatesThatMainWonAndLimitsReapplyingToTheIssueScope()
    {
        var content = ReworkContextWriter.Format(ForceResolvedResult([ClassifierContext]));

        content.Should().Contain("Main is authoritative");
        content.Should().Contain(AgentWorkspacePaths.IssueContextFilePath);
        content.Should().Contain("outside this issue's scope");
    }

    /// <summary>
    /// The diff commands name the old branch head. Restoring a file from it, instead of editing
    /// main's version, reverts main's changes all over again.
    /// </summary>
    [Fact]
    public void Format_ForbidsRestoringFilesFromTheOldBranch()
    {
        var content = ReworkContextWriter.Format(ForceResolvedResult([ClassifierContext]));

        content.Should().Contain("Do not restore a file, or part of one, from your branch's earlier commits");
    }

    [Fact]
    public void Format_GivesTheGitCommandsForTheFullDiffs()
    {
        var content = ReworkContextWriter.Format(ForceResolvedResult([ClassifierContext]));

        content.Should().Contain($"git diff {MergeBase} {PreviousHead} -- src/CiFailureClassifier.cs");
        content.Should().Contain($"git diff {MergeBase} {BaseHead} -- src/CiFailureClassifier.cs");
    }

    [Fact]
    public void Format_TruncatesLongDiffs()
    {
        var longDiff = string.Concat(Enumerable.Range(0, 500).Select(i => $"+line {i}\n"));
        var context = ClassifierContext with { BranchChange = longDiff };

        var content = ReworkContextWriter.Format(ForceResolvedResult([context]));

        content.Should().Contain("+line 0");
        content.Should().NotContain("+line 499");
        content.Should().Contain("more lines");
    }

    [Fact]
    public void Format_ConflictFileWithoutContext_IsStillListed()
    {
        var result = ForceResolvedResult([ClassifierContext]);
        result = new MergeResult
        {
            Success = true,
            HasConflicts = true,
            ForceResolved = true,
            ConflictFiles = ["src/CiFailureClassifier.cs", "src/Other.cs"],
            ForceResolvedContext = result.ForceResolvedContext
        };

        var content = ReworkContextWriter.Format(result);

        content.Should().Contain("`src/Other.cs`");
    }

    private static MergeResult ForceResolvedResult(IReadOnlyList<ForceResolvedFileContext> contexts) => new()
    {
        Success = true,
        HasConflicts = true,
        ForceResolved = true,
        ConflictFiles = contexts.Select(c => c.Path).ToList(),
        MergeBaseSha = MergeBase,
        PreviousHeadSha = PreviousHead,
        BaseHeadSha = BaseHead,
        ForceResolvedContext = contexts
    };
}
