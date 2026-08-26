using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ChatPromptBuilder"/>.
/// Verifies message assembly, first-prompt gating, MCP merge semantics, and passthrough fields.
/// </summary>
public class ChatPromptBuilderTests
{
    private readonly ChatPromptBuilder _sut = new();

    private static PipelineProject MakeProject(
        string id = "proj-1",
        string name = "My Project",
        Dictionary<string, string>? secrets = null,
        string? steering = null,
        IReadOnlyList<McpServerConfig>? mcpServers = null) =>
        new()
        {
            Id = id,
            Name = name,
            Secrets = secrets,
            SteeringContent = steering,
            McpServers = mcpServers
        };

    private static AgentProfile MakeProfile(IReadOnlyList<McpServerConfig>? mcpServers = null) =>
        new()
        {
            Id = "profile-1",
            DisplayName = "Test Profile",
            AgentProviderConfigId = "agent-cfg-1",
            McpServers = mcpServers ?? []
        };

    private static McpServerConfig Stdio(string name, bool disabled = false) =>
        new() { Name = name, Type = "stdio", Disabled = disabled };

    private static ChatPromptParameters BaseParams(
        bool isFirstPrompt = true,
        AgentProfile? profile = null,
        PipelineProject? project = null,
        string? mcpConfigPath = null) =>
        new(
            SessionId: "session-abc",
            Prompt: "Hello agent",
            IsFirstPrompt: isFirstPrompt,
            ChatWindowId: "window-xyz",
            ResolvedProfile: profile,
            SelectedProject: project,
            ResolvedMcpConfigPath: mcpConfigPath ?? "/home/ubuntu/.kiro/settings/mcp.json"
        );

    // ── 1. First prompt — ProjectSecrets, SteeringContent, ProjectId, ProjectName populated ──

    [Fact]
    public void Build_FirstPrompt_IncludesProjectFields()
    {
        var secrets = new Dictionary<string, string> { ["API_KEY"] = "secret" };
        var project = MakeProject(id: "p-1", name: "Alpha", secrets: secrets, steering: "# Steering");
        var p = BaseParams(isFirstPrompt: true, project: project);

        var msg = _sut.Build(p);

        msg.ProjectSecrets.Should().BeEquivalentTo(secrets);
        msg.ProjectSteeringContent.Should().Be("# Steering");
        msg.ProjectId.Should().Be("p-1");
        msg.ProjectName.Should().Be("Alpha");
    }

    // ── 2. Subsequent prompt — ProjectSecrets null (not retransmitted) ──

    [Fact]
    public void Build_NotFirstPrompt_ExcludesProjectFields()
    {
        var secrets = new Dictionary<string, string> { ["API_KEY"] = "secret" };
        var project = MakeProject(secrets: secrets, steering: "# Steering");
        var p = BaseParams(isFirstPrompt: false, project: project);

        var msg = _sut.Build(p);

        msg.ProjectSecrets.Should().BeNull();
        msg.ProjectSteeringContent.Should().BeNull();
        msg.ProjectId.Should().BeNull();
        msg.ProjectName.Should().BeNull();
    }

    // ── 3. No project selected — MCP servers come from profile only ──

    [Fact]
    public void Build_NoProject_UsesMcpServersFromProfile()
    {
        var profile = MakeProfile(mcpServers: [Stdio("context7"), Stdio("web-search")]);
        var p = BaseParams(profile: profile, project: null);

        var msg = _sut.Build(p);

        msg.McpServers.Should().HaveCount(2);
        msg.McpServers.Select(s => s.Name).Should().BeEquivalentTo(["context7", "web-search"]);
    }

    // ── 4. Project selected — MCP servers merged (project overrides profile) ──

    [Fact]
    public void Build_WithProject_MergesMcpServers()
    {
        var profile = MakeProfile(mcpServers: [Stdio("context7"), Stdio("web-search")]);
        // project overrides web-search (disabled) and adds a new server
        var project = MakeProject(mcpServers: [Stdio("web-search", disabled: true), Stdio("sonarqube-mcp")]);
        var p = BaseParams(profile: profile, project: project);

        var msg = _sut.Build(p);

        // context7 (from profile) + web-search (project override, disabled) + sonarqube-mcp (new)
        msg.McpServers.Should().HaveCount(3);
        msg.McpServers.Should().ContainSingle(s => s.Name == "web-search" && s.Disabled);
        msg.McpServers.Should().ContainSingle(s => s.Name == "context7");
        msg.McpServers.Should().ContainSingle(s => s.Name == "sonarqube-mcp");
    }

    // ── 5. No profile, no project — McpServers is empty ──

    [Fact]
    public void Build_NoProfileNoProject_EmptyMcpServers()
    {
        var p = BaseParams(profile: null, project: null);

        var msg = _sut.Build(p);

        msg.McpServers.Should().BeEmpty();
    }

    // ── 6. SessionId, Prompt, ChatWindowId are always set ──

    [Fact]
    public void Build_AlwaysSetsSessionPromptChatWindowId()
    {
        var p = new ChatPromptParameters(
            SessionId: "sid-999",
            Prompt: "test prompt",
            IsFirstPrompt: true,
            ChatWindowId: "wid-42",
            ResolvedProfile: null,
            SelectedProject: null,
            ResolvedMcpConfigPath: null
        );

        var msg = _sut.Build(p);

        msg.SessionId.Should().Be("sid-999");
        msg.Prompt.Should().Be("test prompt");
        msg.ChatWindowId.Should().Be("wid-42");
    }

    // ── 7. UseResume is false on first prompt, true on subsequent ──

    [Fact]
    public void Build_UseResume_ReflectsIsFirstPrompt()
    {
        var first = _sut.Build(BaseParams(isFirstPrompt: true));
        var subsequent = _sut.Build(BaseParams(isFirstPrompt: false));

        first.UseResume.Should().BeFalse();
        subsequent.UseResume.Should().BeTrue();
    }

    // ── 8. McpConfigPath is passed through ──

    [Fact]
    public void Build_McpConfigPath_PassedThrough()
    {
        var p = BaseParams(mcpConfigPath: "/home/ubuntu/.claude.json");

        var msg = _sut.Build(p);

        msg.McpConfigPath.Should().Be("/home/ubuntu/.claude.json");
    }
}
