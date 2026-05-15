using System.Net;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Example-based tests for session selection logic (Property 3).
/// Implements the decision table covering all 5 combinations of
/// UseResume × ResumeSessionId × existing session state.
/// Feature: opencode-agent-executor
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "3")]
public class OpenCodeSessionSelectionTests
{
    /// <summary>
    /// When ResumeSessionId is provided, it takes precedence regardless of UseResume or existing session.
    /// **Validates: Requirements 1.5, 1.6, 11.3**
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResumeSessionId_Provided_UsesProvidedId_RegardlessOfUseResume(bool useResume)
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        var explicitSessionId = "explicit-session-abc";

        // Use URL pattern for message response to avoid SSE consuming it from FIFO queue
        var messageResponse = new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "response text" }]
        };
        ctx.Handler.ForUrlPattern($"/session/{explicitSessionId}/message", messageResponse);

        var request = OpenCodeTestHelpers.CreateRequest(
            prompt: "test",
            useResume: useResume,
            resumeSessionId: explicitSessionId);

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(ExitCodes.Success, result.ExitCode);

        // Verify the POST was made to the explicit session ID
        var messageRequest = ctx.Handler.Requests
            .FirstOrDefault(r => r.Method == HttpMethod.Post && r.Path.Contains("/message"));
        Assert.NotNull(messageRequest);
        Assert.Contains(explicitSessionId, messageRequest.Path);
    }

    /// <summary>
    /// When UseResume=true, ResumeSessionId=null, and an existing session exists,
    /// the provider reuses the existing session (no new session creation).
    /// **Validates: Requirements 1.5, 1.6, 11.3**
    /// </summary>
    [Fact]
    public async Task UseResume_True_ExistingSession_ReusesExistingSession()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        var existingSessionId = "existing-session-001";

        // First call: create a session to establish _currentSessionId
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, existingSessionId);
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "first response" }]
        });

        var firstRequest = OpenCodeTestHelpers.CreateRequest(prompt: "first", useResume: false);
        await ctx.Provider.ExecuteAsync(firstRequest, CancellationToken.None);

        // Second call: UseResume=true should reuse the existing session
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "second response" }]
        });

        var secondRequest = OpenCodeTestHelpers.CreateRequest(prompt: "second", useResume: true);

        // Act
        var result = await ctx.Provider.ExecuteAsync(secondRequest, CancellationToken.None);

        // Assert
        Assert.Equal(ExitCodes.Success, result.ExitCode);

        // Verify the second POST was made to the existing session ID (not a new one)
        var messageRequests = ctx.Handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path.Contains("/message"))
            .ToList();
        Assert.Equal(2, messageRequests.Count);
        Assert.Contains(existingSessionId, messageRequests[1].Path);

        // Verify no second session creation occurred
        var sessionCreateRequests = ctx.Handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path == "/session")
            .ToList();
        Assert.Single(sessionCreateRequests);
    }

    /// <summary>
    /// When UseResume=true, ResumeSessionId=null, and no existing session exists,
    /// the provider creates a new session.
    /// **Validates: Requirements 1.5, 1.6, 11.3**
    /// </summary>
    [Fact]
    public async Task UseResume_True_NoExistingSession_CreatesNewSession()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        var newSessionId = "new-session-from-resume";

        // Enqueue session creation (since no existing session)
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, newSessionId);
        // Use URL pattern for message response
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "response" }]
        });

        var request = OpenCodeTestHelpers.CreateRequest(prompt: "test", useResume: true);

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(ExitCodes.Success, result.ExitCode);

        // Verify a session was created (POST /session)
        var sessionCreateRequest = ctx.Handler.Requests
            .FirstOrDefault(r => r.Method == HttpMethod.Post && r.Path == "/session");
        Assert.NotNull(sessionCreateRequest);

        // Verify the message was sent to the new session
        var messageRequest = ctx.Handler.Requests
            .FirstOrDefault(r => r.Method == HttpMethod.Post && r.Path.Contains("/message"));
        Assert.NotNull(messageRequest);
        Assert.Contains(newSessionId, messageRequest.Path);
    }

    /// <summary>
    /// When UseResume=false, ResumeSessionId=null, and an existing session exists,
    /// the provider calls EnsureSessionAsync which validates the existing session.
    /// If validation returns 404, a new session is created (discards old).
    /// **Validates: Requirements 1.5, 1.6, 11.3**
    /// </summary>
    [Fact]
    public async Task UseResume_False_ExistingSession_CreatesNewSession_DiscardsOld()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        var firstSessionId = "old-session-001";
        var secondSessionId = "new-session-002";

        // First call: create a session to establish _currentSessionId
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, firstSessionId);
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "first response" }]
        });

        var firstRequest = OpenCodeTestHelpers.CreateRequest(prompt: "first", useResume: false);
        await ctx.Provider.ExecuteAsync(firstRequest, CancellationToken.None);

        // Second call: UseResume=false discards _currentSessionId before calling EnsureSessionAsync,
        // so no validation GET occurs — it goes straight to creating a new session.
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, secondSessionId);
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "second response" }]
        });

        var secondRequest = OpenCodeTestHelpers.CreateRequest(prompt: "second", useResume: false);

        // Act
        var result = await ctx.Provider.ExecuteAsync(secondRequest, CancellationToken.None);

        // Assert
        Assert.Equal(ExitCodes.Success, result.ExitCode);

        // Verify a new session was created (second POST /session)
        var sessionCreateRequests = ctx.Handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path == "/session")
            .ToList();
        Assert.Equal(2, sessionCreateRequests.Count);

        // Verify the second message was sent to the NEW session ID
        var messageRequests = ctx.Handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path.Contains("/message"))
            .ToList();
        Assert.Equal(2, messageRequests.Count);
        Assert.Contains(secondSessionId, messageRequests[1].Path);
    }

    /// <summary>
    /// When UseResume=false, ResumeSessionId=null, and no existing session exists,
    /// the provider creates a new session.
    /// **Validates: Requirements 1.5, 1.6, 11.3**
    /// </summary>
    [Fact]
    public async Task UseResume_False_NoExistingSession_CreatesNewSession()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        var newSessionId = "brand-new-session";

        // Enqueue session creation
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, newSessionId);
        // Use URL pattern for message response
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "response" }]
        });

        var request = OpenCodeTestHelpers.CreateRequest(prompt: "test", useResume: false);

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(ExitCodes.Success, result.ExitCode);

        // Verify a session was created (POST /session)
        var sessionCreateRequest = ctx.Handler.Requests
            .FirstOrDefault(r => r.Method == HttpMethod.Post && r.Path == "/session");
        Assert.NotNull(sessionCreateRequest);

        // Verify the message was sent to the new session
        var messageRequest = ctx.Handler.Requests
            .FirstOrDefault(r => r.Method == HttpMethod.Post && r.Path.Contains("/message"));
        Assert.NotNull(messageRequest);
        Assert.Contains(newSessionId, messageRequest.Path);
    }
}
