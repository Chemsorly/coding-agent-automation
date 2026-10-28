namespace CodingAgent.Pipeline.UnitTests.Brain;

using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Models;
using AwesomeAssertions;

public class BrainReviewValidatorTests
{
    private static readonly string _validSummaryJson = """
        {
            "promoted": [],
            "appended": [],
            "retired": [],
            "missedAgain": [],
            "checks": [],
            "flakyTests": [],
            "infrastructureFailures": 0,
            "ciStatusErrors": 0,
            "timeouts": 0
        }
        """;

    private static BrainReviewValidationInput MakeInput(
        IReadOnlyList<BrainPathChange>? changes = null,
        IReadOnlyDictionary<string, string?>? head = null,
        IReadOnlyDictionary<string, string?>? newContents = null,
        IReadOnlyCollection<string>? repos = null,
        string? summary = null) =>
        new BrainReviewValidationInput(
            changes ?? [new BrainPathChange("review/pending.md", BrainPathChangeKind.Modified)],
            head ?? new Dictionary<string, string?>(),
            newContents ?? new Dictionary<string, string?> { ["review/pending.md"] = $"{BrainLayout.PendingMarker}\n" },
            repos ?? [],
            summary ?? _validSummaryJson);

    [Fact]
    public void Validate_ValidChanges_ReturnsIsValid()
    {
        var input = MakeInput();
        var result = BrainReviewValidator.Validate(input);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_DisallowedPath_ReturnsFalse()
    {
        // Req 13.2: a path outside the allowed set is refused
        var input = MakeInput(
            changes: [new BrainPathChange("sessions/data.md", BrainPathChangeKind.Added)],
            newContents: new Dictionary<string, string?> { ["sessions/data.md"] = "data" },
            summary: _validSummaryJson);
        var result = BrainReviewValidator.Validate(input);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(BrainReviewRefusalReasons.DisallowedPath);
    }

    [Fact]
    public void Validate_SkillFileMissingMarker_ReturnsMissingMarker()
    {
        var changes = new[] { new BrainPathChange("general/SKILL.md", BrainPathChangeKind.Modified) };
        var input = MakeInput(
            changes: changes,
            newContents: new Dictionary<string, string?> { ["general/SKILL.md"] = "# Skill\n- [2+] For a, b (#1)." },
            summary: _validSummaryJson);
        var result = BrainReviewValidator.Validate(input);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(BrainReviewRefusalReasons.MissingMarker);
    }

    [Fact]
    public void Validate_SkillFileInvalidLine_ReturnsInvalidLine()
    {
        var badContent = "<!-- brain:v2 skill -->\n# Skill\n## Section\nsome random text";
        var changes = new[] { new BrainPathChange("general/SKILL.md", BrainPathChangeKind.Modified) };
        var input = MakeInput(
            changes: changes,
            newContents: new Dictionary<string, string?> { ["general/SKILL.md"] = badContent },
            summary: _validSummaryJson);
        var result = BrainReviewValidator.Validate(input);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(BrainReviewRefusalReasons.InvalidLine);
    }

    [Fact]
    public void Validate_TechSkillWithUrl_ReturnsSharedLineContent()
    {
        var urlContent = """
            <!-- brain:v2 skill -->
            # Tech

            ## Section
            - [2+] For a, see https://example.com for info (#1).
            """;
        var changes = new[] { new BrainPathChange("technology/dotnet/SKILL.md", BrainPathChangeKind.Added) };
        var input = MakeInput(
            changes: changes,
            newContents: new Dictionary<string, string?> { ["technology/dotnet/SKILL.md"] = urlContent },
            summary: _validSummaryJson);
        var result = BrainReviewValidator.Validate(input);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(BrainReviewRefusalReasons.SharedLineContent);
    }

    [Fact]
    public void Validate_SkillFileShrankTooMuch_ReturnsShrank()
    {
        // Req D8: file with ≥4 lines before, more than half removed = refused
        var headContent = """
            <!-- brain:v2 skill -->
            # Skill

            ## Section
            - [2+] For a, b (#1).
            - [2+] For c, d (#2).
            - [2+] For e, f (#3).
            - [2+] For g, h (#4).
            """;
        var newContent = """
            <!-- brain:v2 skill -->
            # Skill

            ## Section
            - [2+] For a, b (#1).
            """;
        var changes = new[] { new BrainPathChange("general/SKILL.md", BrainPathChangeKind.Modified) };
        var input = MakeInput(
            changes: changes,
            head: new Dictionary<string, string?> { ["general/SKILL.md"] = headContent },
            newContents: new Dictionary<string, string?> { ["general/SKILL.md"] = newContent },
            summary: _validSummaryJson);
        var result = BrainReviewValidator.Validate(input);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(BrainReviewRefusalReasons.Shrank);
    }

    [Fact]
    public void Validate_SmallSkillFile3Lines_ShrinkAllowed()
    {
        // Req D24: shrink rule only for files with >= ShrinkRuleMinLines (4) lines before
        var headContent = """
            <!-- brain:v2 skill -->
            # Skill

            ## Section
            - [2+] For a, b (#1).
            - [2+] For c, d (#2).
            - [2+] For e, f (#3).
            """;
        var changes = new[] { new BrainPathChange("general/SKILL.md", BrainPathChangeKind.Deleted) };
        var input = MakeInput(
            changes: changes,
            head: new Dictionary<string, string?> { ["general/SKILL.md"] = headContent },
            newContents: new Dictionary<string, string?>(),
            summary: _validSummaryJson);
        var result = BrainReviewValidator.Validate(input);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_PendingInvalid_ReturnsReason()
    {
        var badPending = "wrong first line\nsome content";
        var changes = new[] { new BrainPathChange("review/pending.md", BrainPathChangeKind.Modified) };
        var input = MakeInput(
            changes: changes,
            newContents: new Dictionary<string, string?> { ["review/pending.md"] = badPending },
            summary: _validSummaryJson);
        var result = BrainReviewValidator.Validate(input);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(BrainReviewRefusalReasons.PendingInvalid);
    }

    [Fact]
    public void Validate_SummaryNull_ReturnsSummaryMissing()
    {
        // Directly create input with null SummaryJson
        var input = new BrainReviewValidationInput(
            [new BrainPathChange("review/pending.md", BrainPathChangeKind.Modified)],
            new Dictionary<string, string?>(),
            new Dictionary<string, string?> { ["review/pending.md"] = $"{BrainLayout.PendingMarker}\n" },
            [],
            null);  // SummaryJson = null
        var result = BrainReviewValidator.Validate(input);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(BrainReviewRefusalReasons.SummaryMissing);
    }

    [Fact]
    public void Validate_SummaryInvalidJson_ReturnsSummaryInvalid()
    {
        var input = MakeInput(summary: "not json");
        var result = BrainReviewValidator.Validate(input);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(BrainReviewRefusalReasons.SummaryInvalid);
    }

    [Fact]
    public void Validate_InboxDeletion_Allowed()
    {
        var changes = new[] { new BrainPathChange("inbox/2026-10-26_run1.md", BrainPathChangeKind.Deleted) };
        var input = MakeInput(changes: changes, newContents: new Dictionary<string, string?>(),
            summary: _validSummaryJson);
        var result = BrainReviewValidator.Validate(input);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ArchiveMoveAllowed()
    {
        // A non-v2 path deleted + corresponding add under archive/v1/ is allowed
        var changes = new[]
        {
            new BrainPathChange("AGENTS.md", BrainPathChangeKind.Deleted),
            new BrainPathChange("archive/v1/AGENTS.md", BrainPathChangeKind.Added)
        };
        var input = MakeInput(
            changes: changes,
            newContents: new Dictionary<string, string?> { ["archive/v1/AGENTS.md"] = "old content" },
            summary: _validSummaryJson);
        var result = BrainReviewValidator.Validate(input);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_AgentDirectoryIgnored()
    {
        // .agent/ paths are always ignored in validation
        var changes = new[]
        {
            new BrainPathChange(".agent/brain-review-summary.json", BrainPathChangeKind.Added),
            new BrainPathChange("review/pending.md", BrainPathChangeKind.Modified)
        };
        var input = MakeInput(
            changes: changes,
            newContents: new Dictionary<string, string?> { ["review/pending.md"] = $"{BrainLayout.PendingMarker}\n" },
            summary: _validSummaryJson);
        var result = BrainReviewValidator.Validate(input);
        result.IsValid.Should().BeTrue();
    }
}
