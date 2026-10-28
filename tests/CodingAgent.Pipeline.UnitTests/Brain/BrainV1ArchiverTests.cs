using CodingAgent.Pipeline.Brain;
using AwesomeAssertions;

namespace CodingAgent.Pipeline.UnitTests.Brain;

public class BrainV1ArchiverTests
{
    [Fact]
    public void Plan_NonV2Path_PlannedForArchive()
    {
        var files = new[]
        {
            new BrainTrackedFile("AGENTS.md", "first line"),
            new BrainTrackedFile("log.md", "# log"),
            new BrainTrackedFile("sessions/run1.md", "data")
        };
        var moves = BrainV1Archiver.Plan(files);
        moves.Should().HaveCount(3);
        moves.Should().Contain(m => m.From == "AGENTS.md" && m.To == "archive/v1/AGENTS.md");
    }

    [Fact]
    public void Plan_V2PathAlreadyArchived_Ignored()
    {
        var files = new[]
        {
            new BrainTrackedFile("archive/v1/old.md", "data"),
            new BrainTrackedFile(".gitignore", "# git"),
        };
        var moves = BrainV1Archiver.Plan(files);
        moves.Should().BeEmpty();
    }

    [Fact]
    public void Plan_SkillFileWithoutMarker_PlannedForArchive()
    {
        // Req D12: v1 SKILL.md files without the v2 marker are archived
        var files = new[]
        {
            new BrainTrackedFile("general/SKILL.md", "# Skill\n- some lesson"),
        };
        var moves = BrainV1Archiver.Plan(files);
        moves.Should().HaveCount(1);
        moves[0].From.Should().Be("general/SKILL.md");
    }

    [Fact]
    public void Plan_SkillFileWithMarker_LeftAlone()
    {
        var files = new[]
        {
            new BrainTrackedFile("general/SKILL.md", BrainLayout.SkillMarker),
        };
        var moves = BrainV1Archiver.Plan(files);
        moves.Should().BeEmpty();
    }

    [Fact]
    public void Plan_PendingWithoutMarker_PlannedForArchive()
    {
        var files = new[]
        {
            new BrainTrackedFile("review/pending.md", "# pending"),
        };
        var moves = BrainV1Archiver.Plan(files);
        moves.Should().HaveCount(1);
    }

    [Fact]
    public void Plan_PendingWithMarker_LeftAlone()
    {
        var files = new[]
        {
            new BrainTrackedFile("review/pending.md", BrainLayout.PendingMarker),
        };
        var moves = BrainV1Archiver.Plan(files);
        moves.Should().BeEmpty();
    }

    [Fact]
    public void Plan_Idempotent_SecondRunEmpty()
    {
        var files = new[]
        {
            new BrainTrackedFile("AGENTS.md", "first line"),
        };
        var moves1 = BrainV1Archiver.Plan(files);
        // Simulate moving: now the file is under archive/
        var files2 = moves1.Select(m => new BrainTrackedFile(m.To, "first line")).ToList();
        var moves2 = BrainV1Archiver.Plan(files2);
        moves2.Should().BeEmpty();
    }

    [Fact]
    public void BrainReadmeTemplate_ContainsMarker()
    {
        BrainReadmeTemplate.Text.Should().Contain("<!-- brain:v2");
    }

    [Fact]
    public void BrainReadmeTemplate_ContainsSharingSentence()
    {
        BrainReadmeTemplate.Text.Should().Contain("Repositories that must not share this must use separate brains.");
    }
}
