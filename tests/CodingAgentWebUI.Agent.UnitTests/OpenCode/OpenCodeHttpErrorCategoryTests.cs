using System.Net;
using AwesomeAssertions;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Tests that <see cref="OpenCodeAgentProvider.ExecuteAsync"/> surfaces the correct
/// <see cref="AgentErrorCategory"/> for each classified HTTP status code.
///
/// Tests are end-to-end via <c>ExecuteAsync</c> (not the private <c>HandleHttpErrorResponseAsync</c>)
/// so that both the classification logic AND the <c>ErrorCategory</c> forwarding in the final
/// <c>new AgentResult { ... }</c> reconstruction are exercised together.
/// </summary>
[Trait("Feature", "opencode-http-error-category")]
public class OpenCodeHttpErrorCategoryTests
{
    // ── 429 — ProviderRateLimit ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Http429_SetsProviderRateLimitCategory()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-429");
        ctx.Handler.ForUrlPattern("/session/.+/message", HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}");

        var result = await ctx.Provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest(), CancellationToken.None);

        result.ErrorCategory.Should().Be(AgentErrorCategory.ProviderRateLimit);
        result.ExitCode.Should().Be(ExitCodes.GeneralFailure);
        result.OutputLines.Should().ContainMatch("*429*");
    }

    // ── 503 — ProviderOverload ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Http503_SetsProviderOverloadCategory()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-503");
        ctx.Handler.ForUrlPattern("/session/.+/message", HttpStatusCode.ServiceUnavailable, "{\"error\":\"overloaded\"}");

        var result = await ctx.Provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest(), CancellationToken.None);

        result.ErrorCategory.Should().Be(AgentErrorCategory.ProviderOverload);
        result.ExitCode.Should().Be(ExitCodes.GeneralFailure);
        result.OutputLines.Should().ContainMatch("*503*");
    }

    // ── 401 — PermanentAuthFailure ───────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Http401_SetsPermanentAuthFailureCategory()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-401");
        ctx.Handler.ForUrlPattern("/session/.+/message", HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}");

        var result = await ctx.Provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest(), CancellationToken.None);

        result.ErrorCategory.Should().Be(AgentErrorCategory.PermanentAuthFailure);
        result.ExitCode.Should().Be(ExitCodes.GeneralFailure);
        result.OutputLines.Should().ContainMatch("*401*");
    }

    // ── 403 — PermanentAuthFailure ───────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Http403_SetsPermanentAuthFailureCategory()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-403");
        ctx.Handler.ForUrlPattern("/session/.+/message", HttpStatusCode.Forbidden, "{\"error\":\"forbidden\"}");

        var result = await ctx.Provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest(), CancellationToken.None);

        result.ErrorCategory.Should().Be(AgentErrorCategory.PermanentAuthFailure);
        result.ExitCode.Should().Be(ExitCodes.GeneralFailure);
        result.OutputLines.Should().ContainMatch("*403*");
    }

    // ── 404 — None (session eviction path, no category change) ──────────

    [Fact]
    public async Task ExecuteAsync_Http404_HasNoCategoryClassification()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-404");
        ctx.Handler.ForUrlPattern("/session/.+/message", HttpStatusCode.NotFound, "{\"error\":\"not found\"}");

        var result = await ctx.Provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest(), CancellationToken.None);

        result.ErrorCategory.Should().Be(AgentErrorCategory.None,
            "404 triggers session eviction but is not a classified provider error");
        result.ExitCode.Should().Be(ExitCodes.GeneralFailure);
    }

    // ── 500 — None (unclassified server error) ───────────────────────────

    [Fact]
    public async Task ExecuteAsync_Http500_HasNoCategoryClassification()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-500");
        ctx.Handler.ForUrlPattern("/session/.+/message", HttpStatusCode.InternalServerError, "{\"error\":\"internal error\"}");

        var result = await ctx.Provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest(), CancellationToken.None);

        result.ErrorCategory.Should().Be(AgentErrorCategory.None,
            "unclassified HTTP errors must default to None");
        result.ExitCode.Should().Be(ExitCodes.GeneralFailure);
    }

    // ── Success — None ───────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Success_HasNoCategoryClassification()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-ok");
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "done" }]
        });

        var result = await ctx.Provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest(), CancellationToken.None);

        result.ErrorCategory.Should().Be(AgentErrorCategory.None,
            "successful responses must have no error category");
        result.ExitCode.Should().Be(ExitCodes.Success);
    }
}
