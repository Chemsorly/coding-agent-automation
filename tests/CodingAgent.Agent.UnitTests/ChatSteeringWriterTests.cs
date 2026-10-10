using AwesomeAssertions;
using CodingAgent.Pipeline;

namespace CodingAgent.Agent.UnitTests;

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

    // ── Claude Code provider ──────────────────────────────────────────────────

    [Fact]
    public void Write_ClaudeCodeProvider_WritesUserRule_AndNothingInTheWorkspace()
    {
        var rulesDir = Path.Combine(_tempDir, "rules");
        var workspace = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(workspace);

        ChatSteeringWriter.Write("Use TDD.", workspace, CodingAgent.Pipeline.Interfaces.AgentProviderType.ClaudeCode, rulesDir);

        File.ReadAllText(Path.Combine(rulesDir, "pipeline-project.md")).Should().Contain("Use TDD.").And.Contain("# Project Instructions");
        Directory.GetFileSystemEntries(workspace).Should().BeEmpty();
    }

    // ── Kiro CLI provider ─────────────────────────────────────────────────────

    [Fact]
    public void Write_KiroProvider_WritesFileAtExpectedPath()
    {
        ChatSteeringWriter.Write("Use TDD.", _tempDir, CodingAgent.Pipeline.Interfaces.AgentProviderType.KiroCli);

        var expectedPath = Path.Combine(_tempDir, AgentWorkspacePaths.KiroSteeringProjectFilePath);
        File.Exists(expectedPath).Should().BeTrue(
            "Kiro provider must write .kiro/steering/pipeline-project.md");
    }

    [Fact]
    public void Write_KiroProvider_CreatesSteeringDirectory()
    {
        var workspace = Path.Combine(_tempDir, "kiro-workspace");
        Directory.CreateDirectory(workspace);

        ChatSteeringWriter.Write("Always write tests first.", workspace, CodingAgent.Pipeline.Interfaces.AgentProviderType.KiroCli);

        var steeringDir = Path.Combine(workspace, ".kiro", "steering");
        Directory.Exists(steeringDir).Should().BeTrue(
            "Kiro provider must create .kiro/steering/ directory");
    }

    [Fact]
    public void Write_KiroProvider_FileContainsInclusionFrontmatter()
    {
        var content = "Use semantic versioning.";
        ChatSteeringWriter.Write(content, _tempDir, CodingAgent.Pipeline.Interfaces.AgentProviderType.KiroCli);

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
        ChatSteeringWriter.Write(steeringContent, _tempDir, CodingAgent.Pipeline.Interfaces.AgentProviderType.KiroCli);

        var path = Path.Combine(_tempDir, AgentWorkspacePaths.KiroSteeringProjectFilePath);
        var written = File.ReadAllText(path);

        written.Should().Contain(steeringContent,
            "The file body must contain the steering content verbatim");
    }

    [Fact]
    public void Write_KiroProvider_OverwritesPreviousFile()
    {
        ChatSteeringWriter.Write("First steering content.", _tempDir, CodingAgent.Pipeline.Interfaces.AgentProviderType.KiroCli);
        ChatSteeringWriter.Write("Updated steering content.", _tempDir, CodingAgent.Pipeline.Interfaces.AgentProviderType.KiroCli);

        var path = Path.Combine(_tempDir, AgentWorkspacePaths.KiroSteeringProjectFilePath);
        var written = File.ReadAllText(path);

        written.Should().Contain("Updated steering content.");
        written.Should().NotContain("First steering content.");
    }

    // ── OpenCode provider ─────────────────────────────────────────────────────

    private string OpenCodeDir => Path.Combine(_tempDir, "opencode-home");

    [Fact]
    public void Write_OpenCodeProvider_WritesAnInstructionFileOutsideTheWorkspace_AndLeavesAgentsMdAlone()
    {
        var workspace = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "AGENTS.md"), "# The repository's own AGENTS.md");

        ChatSteeringWriter.Write("Use TDD.", workspace, CodingAgent.Pipeline.Interfaces.AgentProviderType.OpenCode,
            openCodeDirectory: OpenCodeDir);

        File.ReadAllText(Path.Combine(OpenCodeDir, "pipeline-project.md"))
            .Should().Contain("# Project Instructions").And.Contain("Use TDD.").And.NotContain("# Repository Instructions");
        File.ReadAllText(Path.Combine(workspace, "AGENTS.md")).Should().Be("# The repository's own AGENTS.md");
        Directory.GetFileSystemEntries(workspace).Should().ContainSingle();
    }

    [Fact]
    public void Write_OpenCodeProvider_ListsTheFilesUnderInstructions_KeepingTheConfigsOtherSections()
    {
        Directory.CreateDirectory(OpenCodeDir);
        File.WriteAllText(Path.Combine(OpenCodeDir, "opencode.json"), """{"mcp":{"docs":{"type":"remote","url":"https://x"}}}""");

        ChatSteeringWriter.Write("Use TDD.", _tempDir, CodingAgent.Pipeline.Interfaces.AgentProviderType.OpenCode, openCodeDirectory: OpenCodeDir);
        ChatSteeringWriter.Write("Again.", _tempDir, CodingAgent.Pipeline.Interfaces.AgentProviderType.OpenCode, openCodeDirectory: OpenCodeDir);

        using var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(OpenCodeDir, "opencode.json")));
        config.RootElement.GetProperty("instructions").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(Path.Combine(OpenCodeDir, "pipeline-*.md"));
        config.RootElement.GetProperty("mcp").GetProperty("docs").GetProperty("url").GetString().Should().Be("https://x");
    }

    [Fact]
    public void Write_OpenCodeProvider_EmptyContent_RemovesTheLastConversationsSteering()
    {
        ChatSteeringWriter.Write("First project.", _tempDir, CodingAgent.Pipeline.Interfaces.AgentProviderType.OpenCode, openCodeDirectory: OpenCodeDir);

        ChatSteeringWriter.Write(string.Empty, _tempDir, CodingAgent.Pipeline.Interfaces.AgentProviderType.OpenCode, openCodeDirectory: OpenCodeDir);

        File.Exists(Path.Combine(OpenCodeDir, "pipeline-project.md")).Should().BeFalse();
    }
}
