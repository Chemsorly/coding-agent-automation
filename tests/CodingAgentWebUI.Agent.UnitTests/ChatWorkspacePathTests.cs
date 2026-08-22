using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for workspace path derivation logic (Req 14).
///
/// The derivation logic (from AgentWorkerService.HandleChatPromptAsync):
///   var chatWorkspace = string.IsNullOrEmpty(message.ChatWindowId)
///       ? AgentDefaults.ChatWorkspacePath
///       : Path.Combine(AgentDefaults.ChatWorkspacesRoot, message.ChatWindowId);
///
/// These tests reference ChatPromptMessage.ChatWindowId and AgentDefaults.ChatWorkspacesRoot,
/// neither of which exist yet. They will FAIL TO COMPILE until tasks 3X.2 and 3X.3 add them.
/// That compile error IS the expected red state for task 3X.1.
/// </summary>
/// <remarks>
/// Validates: Requirements 14
/// </remarks>
public class ChatWorkspacePathTests
{
    /// <summary>
    /// Derives workspace the same way AgentWorkerService.HandleChatPromptAsync does.
    /// Centralised here so all tests use the exact same derivation expression.
    /// </summary>
    private static string DeriveWorkspace(string chatWindowId) =>
        string.IsNullOrEmpty(chatWindowId)
            ? AgentDefaults.ChatWorkspacePath
            : Path.Combine(AgentDefaults.ChatWorkspacesRoot, chatWindowId);

    // ── Test 1: non-empty ChatWindowId → scoped path ──────────────────

    [Fact]
    public void NonEmptyChatWindowId_WorkspaceIsUnderChatWorkspacesRoot()
    {
        // Arrange
        var message = new ChatPromptMessage
        {
            SessionId = "session-1",
            Prompt = "hello",
            ChatWindowId = "abc123"
        };

        // Act
        var workspace = DeriveWorkspace(message.ChatWindowId);

        // Assert
        var expected = Path.Combine(AgentDefaults.ChatWorkspacesRoot, "abc123");
        workspace.Should().Be(expected);
        workspace.Should().StartWith(AgentDefaults.ChatWorkspacesRoot);
    }

    [Fact]
    public void NonEmptyChatWindowId_GuidFormat_WorkspaceIsUnderChatWorkspacesRoot()
    {
        // Arrange — real usage: Guid.NewGuid().ToString() from AgentChat.razor
        var windowId = Guid.NewGuid().ToString();
        var message = new ChatPromptMessage
        {
            SessionId = "session-guid",
            Prompt = "prompt",
            ChatWindowId = windowId
        };

        // Act
        var workspace = DeriveWorkspace(message.ChatWindowId);

        // Assert
        workspace.Should().Be(Path.Combine(AgentDefaults.ChatWorkspacesRoot, windowId));
    }

    // ── Test 2: empty ChatWindowId → static backward-compat path ─────

    [Fact]
    public void EmptyChatWindowId_WorkspaceIsStaticChatWorkspacePath()
    {
        // Arrange — ChatWindowId defaults to "" (backward compat with old SignalR agents)
        var message = new ChatPromptMessage
        {
            SessionId = "session-signalr",
            Prompt = "hello"
            // ChatWindowId omitted → default ""
        };

        // Act
        var workspace = DeriveWorkspace(message.ChatWindowId);

        // Assert
        workspace.Should().Be(AgentDefaults.ChatWorkspacePath);
    }

    [Fact]
    public void ExplicitlyEmptyChatWindowId_WorkspaceIsStaticChatWorkspacePath()
    {
        // Arrange
        var message = new ChatPromptMessage
        {
            SessionId = "session-2",
            Prompt = "hello",
            ChatWindowId = ""
        };

        // Act
        var workspace = DeriveWorkspace(message.ChatWindowId);

        // Assert
        workspace.Should().Be(AgentDefaults.ChatWorkspacePath);
    }

    // ── Test 3: same ChatWindowId → identical workspace ──────────────

    [Fact]
    public void TwoMessagesWithSameChatWindowId_ProduceIdenticalWorkspacePath()
    {
        // Arrange — multiple prompts in one chat window share the same ChatWindowId.
        // The workspace must be identical across all prompts so chat history persists.
        var windowId = Guid.NewGuid().ToString();

        var message1 = new ChatPromptMessage
        {
            SessionId = "session-turn-1",
            Prompt = "first prompt",
            ChatWindowId = windowId
        };

        var message2 = new ChatPromptMessage
        {
            SessionId = "session-turn-2",
            Prompt = "second prompt",
            UseResume = true,
            ChatWindowId = windowId
        };

        // Act
        var workspace1 = DeriveWorkspace(message1.ChatWindowId);
        var workspace2 = DeriveWorkspace(message2.ChatWindowId);

        // Assert — identical workspace ensures Kiro CLI --resume picks up prior session state
        workspace1.Should().Be(workspace2);
    }

    [Fact]
    public void TwoDifferentChatWindowIds_ProduceDifferentWorkspacePaths()
    {
        // Sanity check: two different windows get different workspaces
        var windowId1 = Guid.NewGuid().ToString();
        var windowId2 = Guid.NewGuid().ToString();

        var workspace1 = DeriveWorkspace(windowId1);
        var workspace2 = DeriveWorkspace(windowId2);

        workspace1.Should().NotBe(workspace2);
    }

    // ── Test 4: path traversal — document Path.Combine behavior ──────

    [Fact]
    public void RelativeTraversalChatWindowId_PathCombineIncludesSegmentAsIs()
    {
        // Documents Path.Combine behavior for relative traversals like "../../etc".
        //
        // Path.Combine does NOT sanitise relative traversal segments.
        // "../../etc" is combined as-is, producing a path that walks above
        // ChatWorkspacesRoot on the filesystem.
        //
        // This test documents the behavior, not a security guard.
        // Security is enforced at the dispatch layer (ChatWindowId comes from a
        // server-generated Guid.NewGuid() in AgentChat.razor — user input never
        // reaches this code path directly).

        var traversal = "../../etc";
        var workspace = DeriveWorkspace(traversal);

        // Path.Combine with a non-rooted relative segment: segments are joined with
        // the directory separator. On Windows this produces "C:\...\chat-sessions\..\..\etc";
        // on Linux "/app/workspaces/chat-sessions/../../etc".
        var expected = Path.Combine(AgentDefaults.ChatWorkspacesRoot, traversal);
        workspace.Should().Be(expected,
            because: "Path.Combine does not sanitise relative traversal segments; " +
                     "the caller (AgentChat.razor) is responsible for using only " +
                     "server-generated Guid values as ChatWindowId");
    }

    [Fact]
    public void RootedChatWindowId_PathCombineDiscardsEarlierSegments()
    {
        // Documents Path.Combine behavior for rooted (absolute) segments.
        //
        // When a segment passed to Path.Combine is rooted (starts with / on Linux
        // or a drive letter on Windows), Path.Combine discards all earlier segments
        // and returns the rooted segment alone.
        //
        // e.g. Path.Combine("/app/workspaces/chat-sessions", "/etc") → "/etc"
        //
        // This test documents the behavior so future contributors understand why
        // ChatWindowId must NEVER be user-supplied without validation.

        var rootedSegment = Path.IsPathRooted("/etc") ? "/etc" : @"C:\etc";

        if (!Path.IsPathRooted(rootedSegment))
        {
            // Platform doesn't recognise either form as rooted — skip the test.
            return;
        }

        var workspace = DeriveWorkspace(rootedSegment);

        // Path.Combine discards ChatWorkspacesRoot entirely when the segment is rooted.
        workspace.Should().Be(rootedSegment,
            because: "Path.Combine discards earlier path components when a later segment is rooted; " +
                     "server-generated Guid strings are never rooted so this case cannot occur in production");
    }

    // ── Test 5: constants have expected values ────────────────────────

    [Fact]
    public void ChatWorkspacesRoot_HasExpectedValue()
    {
        AgentDefaults.ChatWorkspacesRoot.Should().Be("/app/workspaces/chat-sessions");
    }

    [Fact]
    public void ChatWorkspacePath_HasExpectedValue()
    {
        // Regression guard — this constant existed before the spec; confirm it wasn't changed.
        AgentDefaults.ChatWorkspacePath.Should().Be("/app/workspaces/chat");
    }

    [Fact]
    public void ChatWorkspacesRoot_AndChatWorkspacePath_AreDistinct()
    {
        // The two paths must not overlap to avoid session isolation issues.
        // "Overlap" means one is a subdirectory of the other — checked by comparing
        // normalised path prefixes (trailing separator ensures "chat" != "chat-sessions").
        AgentDefaults.ChatWorkspacesRoot.Should().NotBe(AgentDefaults.ChatWorkspacePath);

        var root = AgentDefaults.ChatWorkspacesRoot.TrimEnd('/') + "/";
        var chat = AgentDefaults.ChatWorkspacePath.TrimEnd('/') + "/";

        root.Should().NotStartWith(chat,
            because: "ChatWorkspacesRoot must not be a subdirectory of ChatWorkspacePath");
        chat.Should().NotStartWith(root,
            because: "ChatWorkspacePath must not be a subdirectory of ChatWorkspacesRoot");
    }
}
