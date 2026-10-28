using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Models;
using AwesomeAssertions;

namespace CodingAgent.Pipeline.UnitTests.Brain;

public class BrainEvidenceRecorderTests
{
    private static PipelineRun MakeRun() => new PipelineRun
    {
        RunId = "run-1",
        IssueIdentifier = "owner/repo#1",
        IssueTitle = "Test",
        IssueProviderConfigId = "issue-1",
        RepoProviderConfigId = "repo-1"
    };

    private static QualityGateReport MakeFailedTestGate() => new QualityGateReport
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true },
        Tests = new GateResult { GateName = "Tests", Passed = false }
    };

    private static QualityGateReport MakePassedReport() => new QualityGateReport
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true },
        Tests = new GateResult { GateName = "Tests", Passed = true }
    };

    [Fact]
    public void RecordReport_FailedGate_RecordsAttempt()
    {
        var run = MakeRun();
        BrainEvidenceRecorder.RecordReport(run, 1, MakeFailedTestGate());
        run.BrainEvidence!.Attempts.Should().HaveCount(1);
        run.BrainEvidence.Attempts[0].Gate.Should().Be("Tests");
        run.BrainEvidence.Attempts[0].Number.Should().Be(1);
    }

    [Fact]
    public void RecordReport_PassedGate_ClearsPending()
    {
        var run = MakeRun();
        BrainEvidenceRecorder.RecordCommit(run, "abc123");
        BrainEvidenceRecorder.RecordReport(run, 1, MakePassedReport());
        run.BrainEvidence!.Pending.CommitSha.Should().BeNull();
        run.BrainEvidence.Attempts.Should().BeEmpty();
    }

    [Fact]
    public void RecordReport_CapAt10_KeepsFirstAndNewest()
    {
        // Req: keep first + newest 9 = 10 total
        var run = MakeRun();
        for (int i = 1; i <= 12; i++)
            BrainEvidenceRecorder.RecordReport(run, i, MakeFailedTestGate());
        run.BrainEvidence!.Attempts.Should().HaveCount(10);
        run.BrainEvidence.Attempts[0].Number.Should().Be(1); // first kept
        run.BrainEvidence.Attempts[^1].Number.Should().Be(12); // newest kept
    }

    [Fact]
    public void RecordCommit_SetsOnPending()
    {
        var run = MakeRun();
        BrainEvidenceRecorder.RecordCommit(run, "sha1");
        run.BrainEvidence!.Pending.CommitSha.Should().Be("sha1");
    }

    [Fact]
    public void MarkPostPr_SetsFlag()
    {
        var run = MakeRun();
        BrainEvidenceRecorder.MarkPostPr(run);
        run.BrainEvidence!.PostPr.Should().BeTrue();
    }

    [Fact]
    public void RecordFixFingerprints_DetectsChanges()
    {
        var run = MakeRun();
        BrainEvidenceRecorder.RecordReport(run, 1, MakeFailedTestGate());
        var before = new Dictionary<string, string> { ["a.cs"] = "h1", ["b.cs"] = "h2" };
        var after = new Dictionary<string, string> { ["a.cs"] = "h1_new", ["c.cs"] = "h3" };
        BrainEvidenceRecorder.RecordFixFingerprints(run, before, after);
        var attempt = run.BrainEvidence!.Attempts[0];
        attempt.ChangedPaths.Should().BeEquivalentTo(["b.cs", "a.cs", "c.cs"], options => options.WithoutStrictOrdering());
        attempt.NoCodeChange.Should().BeFalse();
    }

    [Fact]
    public void RecordFixFingerprints_EqualFingerprints_NoCodeChange()
    {
        var run = MakeRun();
        BrainEvidenceRecorder.RecordReport(run, 1, MakeFailedTestGate());
        var fp = new Dictionary<string, string> { ["a.cs"] = "h1" };
        BrainEvidenceRecorder.RecordFixFingerprints(run, fp, fp);
        run.BrainEvidence!.Attempts[0].NoCodeChange.Should().BeTrue();
    }

    [Fact]
    public void RecordHumanComments_FiltersBotAndPipelineComments()
    {
        var run = MakeRun();
        var comments = new[]
        {
            new PrConversationComment { Author = "bot-user", CreatedAt = DateTime.UtcNow, Body = "real comment", IsBot = true, IsAuthor = false },
            new PrConversationComment { Author = "human", CreatedAt = DateTime.UtcNow, Body = "real comment", IsBot = false, IsAuthor = false },
            new PrConversationComment { Author = "human2", CreatedAt = DateTime.UtcNow, Body = "🔴 **CRITICAL** finding", IsBot = false, IsAuthor = false },
        };
        BrainEvidenceRecorder.RecordHumanComments(run, comments);
        run.BrainEvidence!.HumanComments.Should().HaveCount(1);
        run.BrainEvidence.HumanComments[0].Author.Should().Be("human");
    }

    [Fact]
    public void RecordHumanComments_KepsNewest10()
    {
        var run = MakeRun();
        var comments = Enumerable.Range(1, 12)
            .Select(i => new PrConversationComment
            {
                Author = $"user{i}",
                CreatedAt = DateTime.UtcNow.AddHours(i),
                Body = $"comment {i}",
                IsBot = false,
                IsAuthor = false
            })
            .ToList();
        BrainEvidenceRecorder.RecordHumanComments(run, comments);
        run.BrainEvidence!.HumanComments.Should().HaveCount(10);
    }
}
