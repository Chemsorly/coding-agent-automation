namespace CodingAgent.Pipeline.UnitTests.Brain;

using CodingAgent.Pipeline.Brain;
using AwesomeAssertions;

public class BrainInboxComposerTests
{
    private static BrainInboxHeader MakeHeader(
        string outcome = "succeeded",
        string? pr = "https://github.com/o/r/pull/1",
        IReadOnlyList<string>? labels = null) =>
        new BrainInboxHeader(
            "run-1",
            "implementation",
            outcome,
            "owner/repo",
            "my-project",
            labels ?? ["dotnet"],
            "#42",
            pr,
            "abc123",
            ["repository", "general"]);

    private static BrainEvidence EmptyEvidence() => new BrainEvidence();

    [Fact]
    public void Compose_StartsWithMarker()
    {
        var result = BrainInboxComposer.Compose(MakeHeader(), null, EmptyEvidence(), null, []);
        result.Text.Should().StartWith(BrainLayout.InboxMarker);
    }

    [Fact]
    public void Compose_HeaderFieldsPresent()
    {
        var result = BrainInboxComposer.Compose(MakeHeader(), null, EmptyEvidence(), null, []);
        result.Text.Should().Contain("- run type: implementation");
        result.Text.Should().Contain("- outcome: succeeded");
        result.Text.Should().Contain("- repository: owner/repo");
        result.Text.Should().Contain("- project: my-project");
        result.Text.Should().Contain("- stack labels: dotnet");
        result.Text.Should().Contain("- issue: #42");
    }

    [Fact]
    public void Compose_NoCandidates_NoSection()
    {
        var result = BrainInboxComposer.Compose(MakeHeader(), null, EmptyEvidence(), null, []);
        result.Text.Should().NotContain("## Candidates");
    }

    [Fact]
    public void Compose_ValidCandidates_IncludeSection()
    {
        var candidates = "- [repository] For a situation, do something (evidence).";
        var result = BrainInboxComposer.Compose(MakeHeader(), candidates, EmptyEvidence(), null, []);
        result.Text.Should().Contain("## Candidates");
        result.KeptCandidates.Should().HaveCount(1);
    }

    [Fact]
    public void Compose_SixCandidates_FifthKeptSixthDropped()
    {
        // Req 3.4: at most 5 candidates
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 6; i++)
            sb.AppendLine($"- [repository] For situation {i}, do something (evidence {i}).");
        var result = BrainInboxComposer.Compose(MakeHeader(), sb.ToString(), EmptyEvidence(), null, []);
        result.KeptCandidates.Should().HaveCount(5);
        result.DroppedCandidates.Should().Be(1);
    }

    [Fact]
    public void Compose_SecretInCandidate_Masked()
    {
        // Req 3.5: secrets are masked
        var canary = "canary-repo-secret-7f3a";
        var candidates = $"- [repository] For a situation, do {canary} (evidence).";
        var secrets = new KeyValuePair<string, string>[]
        {
            new("SECRET_KEY", canary)
        };
        var result = BrainInboxComposer.Compose(MakeHeader(), candidates, EmptyEvidence(), null, secrets);
        result.Text.Should().NotContain(canary);
        result.Text.Should().Contain("***");
    }

    [Fact]
    public void Compose_EmptyEvidence_ShowsReq45Sentence()
    {
        var result = BrainInboxComposer.Compose(MakeHeader(), null, EmptyEvidence(), null, []);
        result.Text.Should().Contain("No failures, findings or human comments.");
    }

    [Fact]
    public void Compose_StackLabels_None_WhenEmpty()
    {
        var header = MakeHeader(labels: []);
        var result = BrainInboxComposer.Compose(header, null, EmptyEvidence(), null, []);
        result.Text.Should().Contain("- stack labels: none");
    }

    [Fact]
    public void Compose_NoPullRequest_LineOmitted()
    {
        var header = MakeHeader(pr: null);
        var result = BrainInboxComposer.Compose(header, null, EmptyEvidence(), null, []);
        result.Text.Should().NotContain("- pull request:");
    }

    [Fact]
    public void Compose_OversizeEvidence_ShortenedAndMarked()
    {
        // Build 12 attempts to force shortening
        var evidence = new BrainEvidence();
        for (int i = 1; i <= 12; i++)
        {
            var attempt = new BrainEvidenceAttempt(
                i, "External CI",
                Enumerable.Range(0, 20).Select(j => $"Test_{i}_{j}").ToList(),
                [new BrainEvidenceCiJob("SonarQube", [], Enumerable.Range(0, 8)
                    .Select(k => $"QUALITY GATE STATUS: FAILED at step {k} with {new string('x', 250)}").ToList())],
                "code", null, [], null);
            evidence.AddAttempt(attempt);
        }
        var result = BrainInboxComposer.Compose(MakeHeader(), null, evidence, null, []);
        result.Shortened.Should().BeTrue();
        result.Text.Should().Contain("shortened to fit 16 KB");
        var bytes = System.Text.Encoding.UTF8.GetByteCount(result.Text);
        bytes.Should().BeLessThan(BrainLayout.MaxInboxBytes + 1);
    }

    [Fact]
    public void ValidateCandidates_TechnologyLabelNotInStack_Dropped()
    {
        // Req 3.4: technology label must be one of the run's stack labels
        var line = "- [technology:java] For a situation, do something (evidence).";
        var result = BrainInboxComposer.ValidateCandidates(line, ["dotnet"]);
        result.Kept.Should().BeEmpty();
        result.Dropped.Should().Be(1);
    }

    [Fact]
    public void Compose_Rewrite_KeepsCandidatesFromExistingText()
    {
        // DD8: rewrite with candidatesText=null + existingText keeps existing candidates
        var existingText = """
            <!-- brain:v2 inbox -->
            # Run run-1
            - run type: implementation
            - outcome: succeeded
            - repository: owner/repo
            - stack labels: dotnet
            - issue: #42
            - skills injected from: none

            ## Candidates
            - [repository] For a situation, do something (evidence).

            ## Evidence
            No failures, findings or human comments.
            """;

        var result = BrainInboxComposer.Compose(MakeHeader(), null, EmptyEvidence(), existingText, []);
        result.KeptCandidates.Should().HaveCount(1);
    }
}
