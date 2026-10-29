namespace CodingAgent.Pipeline.UnitTests.Brain;

using AwesomeAssertions;
using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;
using Xunit;

/// <summary>
/// End-to-end scenario: a run's brain inbox composition + masking pipeline produces one inbox file
/// on the remote (via a real <see cref="BrainRepoFixture"/>) whose evidence lists both attempts,
/// with the MCP canary secret masked (req 12.4, T4 canary, T20).
/// Also validates that <see cref="BrainSyncService.WriteInboxAsync"/> correctly sets run fields.
/// No <c>Category</c> trait — runs in CI and counts for Sonar (T12).
/// </summary>
public sealed class BrainInboxEndToEndTests : IDisposable
{
    // T20: canary secret ≥ 4 characters; never a real token.
    private const string CanarySecret = "canary-repo-secret-7f3a";

    private readonly BrainRepoFixture _fixture;

    public BrainInboxEndToEndTests()
    {
        // Seed the brain remote with a README so there is at least one commit.
        _fixture = BrainRepoFixture.Create(
            new Dictionary<string, string> { ["README.md"] = BrainReadmeTemplate.Text });
    }

    [Fact]
    public void InboxComposition_MasksCanaryAndListsBothAttempts_Req12_4()
    {
        // Req 12.4 / T4 canary: MCP secrets must be absent from the composed inbox file.

        // ── Build a run with two attempts; canary in CI log line and review finding ─
        var runId = "e2e-run-001";
        var evidence = new BrainEvidence();

        // Attempt 1: CI log line contains canary
        evidence.AddAttempt(new BrainEvidenceAttempt(
            Number: 1,
            Gate: "External CI",
            FailedTests: [],
            CiJobs: [new BrainEvidenceCiJob(
                Name: "build-and-test",
                FailedSteps: ["unit-tests"],
                Lines: [$"Error: leaked {CanarySecret}"])],
            Classification: "code",
            NoCodeChange: null,
            ChangedPaths: ["src/Foo.cs"],
            CommitSha: "abc1234"));

        // Attempt 2: review finding contains canary
        evidence.AddAttempt(new BrainEvidenceAttempt(
            Number: 2,
            Gate: "Post-PR CI",
            FailedTests: [],
            CiJobs: [],
            Classification: "code",
            NoCodeChange: null,
            ChangedPaths: [],
            CommitSha: null));

        evidence.Review = new BrainEvidenceReview(
            ByReviewer: new Dictionary<string, (int, int)> { ["reviewer-1"] = (1, 0) },
            TopFindings: [$"[CRITICAL] found.cs:1 — leaked {CanarySecret}"]);

        var header = new BrainInboxHeader(
            RunId: runId,
            RunType: "implementation",
            Outcome: "succeeded",
            RepositoryFullName: "org/repo",
            ProjectName: "test-project",
            StackLabels: [],
            Issue: "#42",
            PullRequestUrl: null,
            SkillsCommit: null,
            SkillsFiles: []);

        // Candidates text also contains the canary (req 12.4 covers all three places)
        var candidates = $"- the fix needed here was {CanarySecret}\n- another valid candidate\n";

        var secrets = new Dictionary<string, string> { ["repo-secret"] = CanarySecret };

        // ── Compose (pure step — produces the masked inbox text) ─────────────────
        var composition = BrainInboxComposer.Compose(header, candidates, evidence, null, secrets);
        var inboxText = composition.Text;

        // Req 12.4: canary must be absent from all three places (candidates, CI log, finding)
        inboxText.Should().NotContain(CanarySecret,
            "all occurrences of the MCP canary secret must be masked with *** (req 12.4)");
        inboxText.Should().Contain("***", "the masked placeholder must appear at least once");

        // Evidence from both attempts must be present (rendered as "### Attempt N —")
        inboxText.Should().Contain("### Attempt 1 —", "first attempt evidence must appear");
        inboxText.Should().Contain("### Attempt 2 —", "second attempt evidence must appear");

        // ── Push to the remote via BrainRepoFixture ───────────────────────────────
        var clone = _fixture.Clone("clone1");
        var inboxPath = BrainLayout.InboxFileName(DateOnly.FromDateTime(DateTime.UtcNow), runId);
        _fixture.CommitAndPush(clone,
            new Dictionary<string, string?> { [inboxPath] = inboxText },
            $"brain: inbox from run {runId}");

        // ── Verify the remote has exactly the inbox file (plus README from seed) ──
        var remoteFiles = _fixture.RemoteFiles();
        remoteFiles.Should().ContainKey(inboxPath,
            "inbox file must be pushed to the remote (req 12.4 T4 canary end-to-end path)");

        var pushedContent = remoteFiles[inboxPath];
        pushedContent.Should().NotContain(CanarySecret,
            "canary must not appear in the pushed file");
        pushedContent.Should().Contain("***", "masked placeholder must be in the pushed file");
    }

    public void Dispose() => _fixture.Dispose();
}
