namespace CodingAgent.Pipeline.UnitTests.Brain;

using CodingAgent.Pipeline.Brain;
using AwesomeAssertions;

public class BrainSkillsAssemblerTests
{
    private static readonly string ValidSkillContent = """
        <!-- brain:v2 skill -->
        # My Skill

        ## Section
        - [2+] For a situation, do something (#1).
        """;

    private static readonly string ValidTechSkillContent = """
        <!-- brain:v2 skill -->
        # Tech Skill

        ## Section
        - [2+] For another situation, do something (#2).
        """;

    [Fact]
    public void Assemble_EmptyFiles_ReturnsEmpty()
    {
        var result = BrainSkillsAssembler.Assemble(
            new Dictionary<string, string>(), "repo", ["dotnet"]);
        result.Result.Should().Be(BrainSkillsResults.Empty);
        result.Bytes.Should().Be(0);
    }

    [Fact]
    public void Assemble_OrderIsRepoThenTechThenGeneral()
    {
        var files = new Dictionary<string, string>
        {
            [BrainLayout.GeneralSkillPath] = ValidSkillContent,
            ["projects/myrepo/SKILL.md"] = ValidSkillContent,
            ["technology/dotnet/SKILL.md"] = ValidTechSkillContent
        };
        var result = BrainSkillsAssembler.Assemble(files, "myrepo", ["dotnet"]);
        result.FilesInjected.Should().ContainInOrder(
            "projects/myrepo/SKILL.md",
            "technology/dotnet/SKILL.md",
            BrainLayout.GeneralSkillPath);
    }

    [Fact]
    public void Assemble_MissingFile_Skipped()
    {
        var files = new Dictionary<string, string>
        {
            [BrainLayout.GeneralSkillPath] = ValidSkillContent
        };
        var result = BrainSkillsAssembler.Assemble(files, "myrepo", []);
        result.FilesInjected.Should().ContainSingle().Which.Should().Be(BrainLayout.GeneralSkillPath);
    }

    [Fact]
    public void Assemble_NoMarker_SkippedWithReason()
    {
        var files = new Dictionary<string, string>
        {
            [BrainLayout.GeneralSkillPath] = "# Skill\n- [2+] For a, b (#1)."
        };
        var result = BrainSkillsAssembler.Assemble(files, null, []);
        result.Result.Should().Be(BrainSkillsResults.SkippedInvalid);
        result.FilesSkipped.Should().ContainSingle().Which.Reason.Should().Be("missing_marker");
    }

    [Fact]
    public void Assemble_TextStartsWithHeaderSentence()
    {
        var files = new Dictionary<string, string>
        {
            [BrainLayout.GeneralSkillPath] = ValidSkillContent
        };
        var result = BrainSkillsAssembler.Assemble(files, null, []);
        result.Text.Should().StartWith("Lessons this pipeline learned from earlier runs.");
    }

    [Fact]
    public void Assemble_MarkerRemovedFromOutput()
    {
        var files = new Dictionary<string, string>
        {
            [BrainLayout.GeneralSkillPath] = ValidSkillContent
        };
        var result = BrainSkillsAssembler.Assemble(files, null, []);
        result.Text.Should().NotContain("<!-- brain:v2 skill -->");
    }

    [Fact]
    public void Assemble_InjectedResult_WhenAllValid()
    {
        var files = new Dictionary<string, string>
        {
            [BrainLayout.GeneralSkillPath] = ValidSkillContent
        };
        var result = BrainSkillsAssembler.Assemble(files, null, []);
        result.Result.Should().Be(BrainSkillsResults.Injected);
    }

    [Fact]
    public void Assemble_TechFileWithUrl_SkippedWithSharedLineContent()
    {
        // Req 6.4: technology files must not contain URLs
        var techContent = """
            <!-- brain:v2 skill -->
            # Tech

            ## Section
            - [2+] For a, see https://example.com (#1).
            """;
        var files = new Dictionary<string, string>
        {
            ["technology/dotnet/SKILL.md"] = techContent
        };
        var result = BrainSkillsAssembler.Assemble(files, null, ["dotnet"]);
        result.Result.Should().Be(BrainSkillsResults.SkippedInvalid);
        result.FilesSkipped.Should().ContainSingle().Which.Reason.Should().Be("shared_line_content");
    }

    [Fact]
    public void Assemble_StackLabelsDeduplicated()
    {
        // Same label twice — should only include the technology skill once
        var files = new Dictionary<string, string>
        {
            ["technology/dotnet/SKILL.md"] = ValidTechSkillContent
        };
        var result = BrainSkillsAssembler.Assemble(files, null, ["dotnet", "DOTNET"]);
        result.FilesInjected.Should().ContainSingle();
    }
}
