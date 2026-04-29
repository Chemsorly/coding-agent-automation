using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for the chat prompt handling in <see cref="AgentWorkerService"/>,
/// specifically the MCP config writing and null/empty guard behavior.
/// </summary>
public class ChatPromptTests
{
    [Fact]
    public void ChatPromptMessage_DefaultMcpServers_IsEmptyNotNull()
    {
        var message = new ChatPromptMessage
        {
            SessionId = "test-session",
            Prompt = "hello"
        };

        message.McpServers.Should().NotBeNull();
        message.McpServers.Should().BeEmpty();
    }

    [Fact]
    public void ChatPromptMessage_DefaultMcpConfigPath_IsKiroCliGlobal()
    {
        var message = new ChatPromptMessage
        {
            SessionId = "test-session",
            Prompt = "hello"
        };

        message.McpConfigPath.Should().Be("/home/ubuntu/.kiro/settings/mcp.json");
    }

    [Fact]
    public void ChatPromptMessage_CustomMcpConfigPath_IsPreserved()
    {
        var message = new ChatPromptMessage
        {
            SessionId = "test-session",
            Prompt = "hello",
            McpConfigPath = "/home/ubuntu/.claude.json"
        };

        message.McpConfigPath.Should().Be("/home/ubuntu/.claude.json");
    }

    [Fact]
    public void ChatPromptMessage_WithMcpServers_CountIsCorrect()
    {
        var servers = new List<McpServerConfig>
        {
            new() { Name = "context7", Command = "uvx", Args = ["context7-mcp"] },
            new() { Name = "web-search", Type = "http", Url = "https://example.com/mcp" }
        };

        var message = new ChatPromptMessage
        {
            SessionId = "test-session",
            Prompt = "list tools",
            McpServers = servers
        };

        message.McpServers.Should().HaveCount(2);
        message.McpServers[0].Name.Should().Be("context7");
        message.McpServers[1].Type.Should().Be("http");
    }

    [Fact]
    public void ChatCompletedMessage_WithError_PreservesFields()
    {
        var message = new ChatCompletedMessage
        {
            SessionId = "test-session",
            ExitCode = 1,
            Error = "Access denied"
        };

        message.SessionId.Should().Be("test-session");
        message.ExitCode.Should().Be(1);
        message.Error.Should().Be("Access denied");
    }

    [Fact]
    public void ChatCompletedMessage_Success_ErrorIsNull()
    {
        var message = new ChatCompletedMessage
        {
            SessionId = "test-session",
            ExitCode = 0
        };

        message.ExitCode.Should().Be(0);
        message.Error.Should().BeNull();
    }

    [Fact]
    public void ChatResponseMessage_WithLines_PreservesContent()
    {
        var message = new ChatResponseMessage
        {
            SessionId = "test-session",
            Lines = new List<string> { "Hello!", "I can help with that." }
        };

        message.Lines.Should().HaveCount(2);
        message.Lines[0].Should().Be("Hello!");
    }
}
