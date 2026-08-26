using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for ChatPromptBuilder (pure logic — no I/O).
/// Covers: MCP merge, first/subsequent prompt field gating, defaults.
/// </summary>
public sealed class ChatPromptBuilderTests
{
    private readonly ChatPromptBuilder _sut = new();

    private static ChatPromptParameters MakeParams(
        bool isFirstPrompt = true,
        AgentProfile? profile = null,
        PipelineProject? project = null,
        string? mcpConfigPath = null) =>
        new(
            SessionId: "session-1",
            Prompt: "Write me a test",
            IsFirstPrompt: isFirstPrompt,
            ChatWindowId: "window-1",
            ResolvedProfile: profile,
            SelectedProject: project,
            ResolvedMcpConfigPath: mcpConfigPath);

    // ── Basic fields ──────────────────────────────────────────────────────

    [Fact]
    public void Build_SetsSessionId()
    {
        var msg = _sut.Build(MakeParams());
        msg.SessionId.Should().Be("session-1");
    }

    [Fact]
    public void Build_SetsPrompt()
    {
        var msg = _sut.Build(MakeParams());
        msg.Prompt.Should().Be("Write me a test");
    }

    [Fact]
    public void Build_FirstPrompt_UseResumeFalse()
    {
        var msg = _sut.Build(MakeParams(isFirstPrompt: true));
        msg.UseResume.Should().BeFalse();
    }

    [Fact]
    public void Build_SubsequentPrompt_UseResumeTrue()
    {
        var msg = _sut.Build(MakeParams(isFirstPrompt: false));
        msg.UseResume.Should().BeTrue();
    }

    // ── McpConfigPath default ─────────────────────────────────────────────

    [Fact]
    public void Build_NoMcpConfigPath_UsesKiroDefault()
    {
        var msg = _sut.Build(MakeParams(mcpConfigPath: null));
        msg.McpConfigPath.Should().Be("/home/ubuntu/.kiro/settings/mcp.json");
    }

    [Fact]
    public void Build_WithMcpConfigPath_UsesProvided()
    {
        var msg = _sut.Build(MakeParams(mcpConfigPath: "/custom/path.json"));
        msg.McpConfigPath.Should().Be("/custom/path.json");
    }

    // ── First-prompt-only fields ──────────────────────────────────────────

    [Fact]
    public void Build_FirstPrompt_WithProject_SetsProjectIdAndName()
    {
        var project = new PipelineProject { Id = "proj-1", Name = "MyProject" };
        var msg = _sut.Build(MakeParams(isFirstPrompt: true, project: project));

        msg.ProjectId.Should().Be("proj-1");
        msg.ProjectName.Should().Be("MyProject");
    }

    [Fact]
    public void Build_SubsequentPrompt_DoesNotSendProjectIdentity()
    {
        var project = new PipelineProject { Id = "proj-1", Name = "MyProject" };
        var msg = _sut.Build(MakeParams(isFirstPrompt: false, project: project));

        msg.ProjectId.Should().BeNull();
        msg.ProjectName.Should().BeNull();
    }

    [Fact]
    public void Build_FirstPrompt_WithProjectSecrets_SetsSecrets()
    {
        var project = new PipelineProject
        {
            Id = "proj-1",
            Name = "P",
            Secrets = new Dictionary<string, string> { ["TOKEN"] = "secret123" }
        };
        var msg = _sut.Build(MakeParams(isFirstPrompt: true, project: project));

        msg.ProjectSecrets.Should().NotBeNull();
        msg.ProjectSecrets!["TOKEN"].Should().Be("secret123");
    }

    [Fact]
    public void Build_SubsequentPrompt_DoesNotSendSecrets()
    {
        var project = new PipelineProject
        {
            Id = "proj-1",
            Name = "P",
            Secrets = new Dictionary<string, string> { ["TOKEN"] = "secret" }
        };
        var msg = _sut.Build(MakeParams(isFirstPrompt: false, project: project));

        msg.ProjectSecrets.Should().BeNull();
    }

    [Fact]
    public void Build_SubsequentPrompt_DoesNotSendSteeringContent()
    {
        var project = new PipelineProject { Id = "p", Name = "P", SteeringContent = "some steering" };
        var msg = _sut.Build(MakeParams(isFirstPrompt: false, project: project));

        msg.ProjectSteeringContent.Should().BeNull();
    }

    [Fact]
    public void Build_FirstPrompt_WithSteering_SetsSteering()
    {
        var project = new PipelineProject { Id = "p", Name = "P", SteeringContent = "## Rules" };
        var msg = _sut.Build(MakeParams(isFirstPrompt: true, project: project));

        msg.ProjectSteeringContent.Should().Be("## Rules");
    }

    // ── MCP server merging ────────────────────────────────────────────────

    [Fact]
    public void Build_NoProjectNorProfile_EmptyMcpServers()
    {
        var msg = _sut.Build(MakeParams(profile: null, project: null));
        msg.McpServers.Should().BeEmpty();
    }

    [Fact]
    public void Build_ProfileOnlyServers_PassedThrough()
    {
        var profile = new AgentProfile
        {
            Id = "p",
            DisplayName = "P",
            AgentProviderConfigId = "k",
            McpServers = [new McpServerConfig { Name = "profile-mcp", Type = "stdio", Command = "node" }]
        };
        var msg = _sut.Build(MakeParams(profile: profile, project: null));

        msg.McpServers.Should().HaveCount(1);
        msg.McpServers[0].Name.Should().Be("profile-mcp");
    }

    [Fact]
    public void Build_ProjectMcpOverridesProfileMcpByName()
    {
        var profile = new AgentProfile
        {
            Id = "p",
            DisplayName = "P",
            AgentProviderConfigId = "k",
            McpServers = [new McpServerConfig { Name = "shared-mcp", Type = "stdio", Command = "old-cmd" }]
        };
        var project = new PipelineProject
        {
            Id = "proj",
            Name = "P",
            McpServers = [new McpServerConfig { Name = "shared-mcp", Type = "stdio", Command = "new-cmd" }]
        };
        var msg = _sut.Build(MakeParams(profile: profile, project: project));

        // Project overrides profile — same name should use project's version
        var server = msg.McpServers.FirstOrDefault(s => s.Name == "shared-mcp");
        server.Should().NotBeNull();
        server!.Command.Should().Be("new-cmd");
    }

    [Fact]
    public void Build_ProjectAddsNewMcpServer()
    {
        var profile = new AgentProfile
        {
            Id = "p",
            DisplayName = "P",
            AgentProviderConfigId = "k",
            McpServers = [new McpServerConfig { Name = "profile-mcp", Type = "stdio", Command = "a" }]
        };
        var project = new PipelineProject
        {
            Id = "proj",
            Name = "P",
            McpServers = [new McpServerConfig { Name = "project-mcp", Type = "stdio", Command = "b" }]
        };
        var msg = _sut.Build(MakeParams(profile: profile, project: project));

        // Both servers should be present
        msg.McpServers.Should().HaveCount(2);
        msg.McpServers.Select(s => s.Name).Should().Contain("profile-mcp").And.Contain("project-mcp");
    }

    [Fact]
    public void Build_NullProject_ProfileMcpPassedUnchanged()
    {
        var profile = new AgentProfile
        {
            Id = "p",
            DisplayName = "P",
            AgentProviderConfigId = "k",
            McpServers = [new McpServerConfig { Name = "m1", Type = "stdio", Command = "x" }]
        };
        var msg = _sut.Build(MakeParams(profile: profile, project: null));

        msg.McpServers.Should().HaveCount(1);
    }
}
