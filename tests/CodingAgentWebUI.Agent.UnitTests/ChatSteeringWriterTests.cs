using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="ChatSteeringWriter"/>.
/// Verifies Kiro CLI and OpenCode steering file writing for the chat path.
/// The chat path has only ProjectSteeringContent (no repo steering) — tests reflect this.
/// </summary>
public class ChatSteeringWriterTests : IDisposable
{
    private readonly string _tempDir;

    public ChatSteeringWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"chat-steering-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    // ── Kiro CLI provider ─────────────────────────────────────────────────────

    [Fact]
    public void Write_KiroProvider_WritesFileAtExpectedPath()
    {
        ChatSteeringWriter.Write("Use TDD.", _tempDir, isOpenCodeProvider: false);

        var expectedPath = Path.Combine(_tempDir, AgentWorkspacePaths.KiroSteeringProjectFilePath);
        File.Exists(expectedPath).Should().BeTrue(
            "Kiro provider must write .kiro/steering/pipeline-project.md");
    }

    [Fact]
    public void Write_KiroProvider_CreatesSteeringDirectory()
    {
        var workspace = Path.Combine(_tempDir, "kiro-workspace");
        Directory.CreateDirectory(workspace);

        ChatSteeringWriter.Write("Always write tests first.", workspace, isOpenCodeProvider: false);

        var steeringDir = Path.Combine(workspace, ".kiro", "steering");
        Directory.Exists(steeringDir).Should().BeTrue(
            "Kiro provider must create .kiro/steering/ directory");
    }

    [Fact]
    public void Write_KiroProvider_FileContainsInclusionFrontmatter()
    {
        var content = "Use semantic versioning.";
        ChatSteeringWriter.Write(content, _tempDir, isOpenCodeProvider: false);

        var path = Path.Combine(_tempDir, AgentWorkspacePaths.KiroSteeringProjectFilePath);
        var written = File.ReadAllText(path);

        written.Should().Contain("inclusion: always",
            "Kiro steering files must include the 'inclusion: always' frontmatter");
        written.Should().Contain("Written by automation pipeline",
            "Kiro steering files must include the auto-generated comment");
    }

    [Fact]
    public void Write_KiroProvider_FileBodyContainsSteeeringContent()
    {
        var steeringContent = "Follow conventional commits.\nUse feature branches.";
        ChatSteeringWriter.Write(steeringContent, _tempDir, isOpenCodeProvider: false);

        var path = Path.Combine(_tempDir, AgentWorkspacePaths.KiroSteeringProjectFilePath);
        var written = File.ReadAllText(path);

        written.Should().Contain(steeringContent,
            "The file body must contain the steering content verbatim");
    }

    [Fact]
    public void Write_KiroProvider_OverwritesPreviousFile()
    {
        ChatSteeringWriter.Write("First steering content.", _tempDir, isOpenCodeProvider: false);
        ChatSteeringWriter.Write("Updated steering content.", _tempDir, isOpenCodeProvider: false);

        var path = Path.Combine(_tempDir, AgentWorkspacePaths.KiroSteeringProjectFilePath);
        var written = File.ReadAllText(path);

        written.Should().Contain("Updated steering content.");
        written.Should().NotContain("First steering content.");
    }

    // ── OpenCode provider ─────────────────────────────────────────────────────

    [Fact]
    public void Write_OpenCodeProvider_WritesAgentsMdAtExpectedPath()
    {
        ChatSteeringWriter.Write("Use TDD.", _tempDir, isOpenCodeProvider: true);

        var expectedPath = Path.Combine(_tempDir, AgentWorkspacePaths.OpenCodeAgentsFilePath);
        File.Exists(expectedPath).Should().BeTrue(
            "OpenCode provider must write AGENTS.md");
    }

    [Fact]
    public void Write_OpenCodeProvider_AgentsMdContainsProjectInstructionsSection()
    {
        var content = "Always write tests before implementation.";
        ChatSteeringWriter.Write(content, _tempDir, isOpenCodeProvider: true);

        var path = Path.Combine(_tempDir, AgentWorkspacePaths.OpenCodeAgentsFilePath);
        var written = File.ReadAllText(path);

        written.Should().Contain("# Project Instructions",
            "OpenCode pipeline block must include # Project Instructions section");
        written.Should().Contain(content,
            "OpenCode pipeline block must include the steering content");
    }

    [Fact]
    public void Write_OpenCodeProvider_AgentsMdContainsPipelineMarkers()
    {
        ChatSteeringWriter.Write("Some instructions.", _tempDir, isOpenCodeProvider: true);

        var path = Path.Combine(_tempDir, AgentWorkspacePaths.OpenCodeAgentsFilePath);
        var written = File.ReadAllText(path);

        written.Should().Contain("<!-- BEGIN PIPELINE STEERING",
            "OpenCode pipeline block must start with BEGIN marker");
        written.Should().Contain("<!-- END PIPELINE STEERING -->",
            "OpenCode pipeline block must end with END marker");
    }

    [Fact]
    public void Write_OpenCodeProvider_ReplacesExistingPipelineBlock()
    {
        // Write first block
        ChatSteeringWriter.Write("First steering content.", _tempDir, isOpenCodeProvider: true);

        // Write second block — should replace the first, not append
        ChatSteeringWriter.Write("Second steering content.", _tempDir, isOpenCodeProvider: true);

        var path = Path.Combine(_tempDir, AgentWorkspacePaths.OpenCodeAgentsFilePath);
        var written = File.ReadAllText(path);

        written.Should().Contain("Second steering content.");
        written.Should().NotContain("First steering content.",
            "Re-writing must replace the existing pipeline block, not duplicate it");
    }

    [Fact]
    public void Write_OpenCodeProvider_PreservesExistingUserContentBelowBlock()
    {
        var agentsPath = Path.Combine(_tempDir, AgentWorkspacePaths.OpenCodeAgentsFilePath);
        var userContent = "# My own AGENTS.md\nDo not use tabs.";
        File.WriteAllText(agentsPath, userContent);

        ChatSteeringWriter.Write("Pipeline instructions.", _tempDir, isOpenCodeProvider: true);

        var written = File.ReadAllText(agentsPath);

        written.Should().Contain(userContent,
            "Existing user content must be preserved below the pipeline block");
        written.Should().Contain("Pipeline instructions.",
            "New pipeline block must be present");
    }

    [Fact]
    public void Write_OpenCodeProvider_PipelineBlockIsPrependedBeforeUserContent()
    {
        var agentsPath = Path.Combine(_tempDir, AgentWorkspacePaths.OpenCodeAgentsFilePath);
        File.WriteAllText(agentsPath, "# User Content");

        ChatSteeringWriter.Write("Pipeline first.", _tempDir, isOpenCodeProvider: true);

        var written = File.ReadAllText(agentsPath);

        // BEGIN marker must appear before user content
        var beginIndex = written.IndexOf("BEGIN PIPELINE STEERING", StringComparison.Ordinal);
        var userIndex = written.IndexOf("# User Content", StringComparison.Ordinal);

        beginIndex.Should().BeLessThan(userIndex,
            "Pipeline block must be prepended before existing user content");
    }

    [Fact]
    public void Write_OpenCodeProvider_OnlyProjectInstructionsSection_NoRepositorySection()
    {
        // The chat path has no RepoSteeringContent — must not emit a # Repository Instructions section
        ChatSteeringWriter.Write("Project content only.", _tempDir, isOpenCodeProvider: true);

        var path = Path.Combine(_tempDir, AgentWorkspacePaths.OpenCodeAgentsFilePath);
        var written = File.ReadAllText(path);

        written.Should().NotContain("# Repository Instructions",
            "Chat path has no RepoSteeringContent — must not emit a Repository Instructions section");
    }

    // ── BuildChatBlock (internal, tested directly) ────────────────────────────

    [Fact]
    public void BuildChatBlock_ContainsBeginAndEndMarkers()
    {
        var block = ChatSteeringWriter.BuildChatBlock("Some content.");

        block.Should().StartWith("<!-- BEGIN PIPELINE STEERING");
        block.Should().Contain("<!-- END PIPELINE STEERING -->");
    }

    [Fact]
    public void BuildChatBlock_ContainsProjectInstructionsHeader()
    {
        var block = ChatSteeringWriter.BuildChatBlock("content");

        block.Should().Contain("# Project Instructions");
    }

    [Fact]
    public void BuildChatBlock_ContainsProvidedContent()
    {
        var content = "Always prefer composition over inheritance.";
        var block = ChatSteeringWriter.BuildChatBlock(content);

        block.Should().Contain(content);
    }

    [Fact]
    public void BuildChatBlock_DoesNotContainRepositoryInstructionsHeader()
    {
        var block = ChatSteeringWriter.BuildChatBlock("proj content");

        block.Should().NotContain("# Repository Instructions",
            "Chat block is single-source (project only) — no repo section");
    }
}
