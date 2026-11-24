using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgent.Agent.OpenCode;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;
using AwesomeAssertions;

namespace CodingAgent.Agent.UnitTests.OpenCode;

/// <summary>
/// Property-based tests for request body correctness (Property 12).
/// Verifies that the outbound POST /session/:id/message contains a JSON body with
/// structure { "parts": [{ "type": "text", "text": "&lt;prompt&gt;" }] } where the prompt
/// is the exact input string (no escaping beyond JSON serialization).
/// BuildTextPart_IncludesPromptText_InRequestBody checks the same through a real ExecuteAsync call.
/// Feature: opencode-agent-executor
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "12")]
public class OpenCodeRequestBodyPropertyTests
{
    /// <summary>
    /// Property 12: Request Body Correctness
    /// For any prompt string, the outbound HTTP request to POST /session/:id/message SHALL
    /// contain a JSON body with structure { "parts": [{ "type": "text", "text": "&lt;prompt&gt;" }] }
    /// where &lt;prompt&gt; is the exact input string (no escaping beyond JSON serialization).
    /// **Validates: Requirements 1.2, 3.4**
    /// </summary>
    [Property(Arbitrary = [typeof(PromptStringArbitrary)], MaxTest = 20)]
    public async Task RequestBody_ContainsExactPromptInCorrectStructure(PromptInput input)
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        // Enqueue session creation response
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler);

        // Enqueue a successful message response so ExecuteAsync completes
        ctx.Handler.ForUrlPattern("/session/.+/message",
            new SendMessageResponse { Parts = [new MessagePart { Type = "text", Text = "ok" }] });

        var request = OpenCodeTestHelpers.CreateRequest(prompt: input.Prompt);

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        // Assert: execution should succeed
        Assert.Equal(ExitCodes.Success, result.ExitCode);

        // Find the POST /session/:id/message request
        var messageRequest = ctx.Handler.Requests
            .FirstOrDefault(r => r.Method == HttpMethod.Post &&
                                 r.Path.Contains("/message"));

        Assert.NotNull(messageRequest);
        Assert.NotNull(messageRequest.Body);

        // Deserialize the request body
        var body = JsonSerializer.Deserialize<JsonElement>(messageRequest.Body);

        // Verify structure: must have "parts" array
        Assert.True(body.TryGetProperty("parts", out var partsElement),
            "Request body must contain 'parts' property");
        Assert.Equal(JsonValueKind.Array, partsElement.ValueKind);

        // Verify parts array has exactly one element
        Assert.Equal(1, partsElement.GetArrayLength());

        var part = partsElement[0];

        // Verify the part has "type": "text"
        Assert.True(part.TryGetProperty("type", out var typeElement),
            "Part must contain 'type' property");
        Assert.Equal("text", typeElement.GetString());

        // Verify the part has "text": "<exact prompt>"
        Assert.True(part.TryGetProperty("text", out var textElement),
            "Part must contain 'text' property");
        Assert.Equal(input.Prompt, textElement.GetString());
    }

    /// <summary>
    /// Property 12 (sub-property): Optional model field MAY be included if configured.
    /// When a model is configured, the request body includes a "model" field.
    /// When no model is configured, the "model" field is absent or null.
    /// **Validates: Requirements 1.2, 3.4**
    /// </summary>
    [Property(Arbitrary = [typeof(PromptStringArbitrary)], MaxTest = 20)]
    public async Task RequestBody_CarriesTheConfiguredModelAsAModelRef(PromptInput input)
    {
        // Arrange — create context WITH a model configured
        var modelName = "anthropic/claude-sonnet-4-20250514";
        var ctx = OpenCodeTestHelpers.CreateTestContext(model: modelName);

        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler);
        ctx.Handler.ForUrlPattern("/session/.+/message",
            new SendMessageResponse { Parts = [new MessagePart { Type = "text", Text = "ok" }] });

        var request = OpenCodeTestHelpers.CreateRequest(prompt: input.Prompt);

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(ExitCodes.Success, result.ExitCode);

        var messageRequest = ctx.Handler.Requests
            .FirstOrDefault(r => r.Method == HttpMethod.Post &&
                                 r.Path.Contains("/message"));

        Assert.NotNull(messageRequest);
        Assert.NotNull(messageRequest.Body);

        var body = JsonSerializer.Deserialize<JsonElement>(messageRequest.Body);

        // The configured provider/model goes in as OpenCode's model ref
        var model = body.GetProperty("model");
        Assert.Equal("anthropic", model.GetProperty("providerID").GetString());
        Assert.Equal("claude-sonnet-4-20250514", model.GetProperty("modelID").GetString());

        // Verify the prompt is still correct
        var partsElement = body.GetProperty("parts");
        Assert.Equal(1, partsElement.GetArrayLength());
        Assert.Equal(input.Prompt, partsElement[0].GetProperty("text").GetString());
    }

    // ── Light tier ────────────────────────────────────────────────────────

    private const string RouteModel = "anthropic/claude-sonnet-4-20250514";
    private const string LightModelId = "anthropic/claude-haiku-4-5";

    /// <summary>Sends <paramref name="request"/> and returns the JSON body of the POST /session/:id/message it made.</summary>
    private static async Task<JsonElement> SendAndReadMessageBodyAsync(
        OpenCodeTestContext ctx, AgentRequest request, bool sessionExists = false)
    {
        if (!sessionExists)
            OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler);
        ctx.Handler.ForUrlPattern("/session/.+/message",
            new SendMessageResponse { Parts = [new MessagePart { Type = "text", Text = "ok" }] });

        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        var messageRequest = ctx.Handler.Requests
            .FirstOrDefault(r => r.Method == HttpMethod.Post && r.Path.Contains("/message"));
        Assert.NotNull(messageRequest);
        Assert.NotNull(messageRequest.Body);
        return JsonSerializer.Deserialize<JsonElement>(messageRequest.Body);
    }

    private static void AssertModelRef(JsonElement body, string providerId, string modelId)
    {
        var model = body.GetProperty("model");
        Assert.Equal(providerId, model.GetProperty("providerID").GetString());
        Assert.Equal(modelId, model.GetProperty("modelID").GetString());
    }

    /// <summary>A call that starts a fresh session and asks for the light tier sends the light model.</summary>
    [Property(Arbitrary = [typeof(PromptStringArbitrary)], MaxTest = 20)]
    public async Task RequestBody_LightCall_CarriesTheLightModel(PromptInput input)
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext(model: RouteModel, lightModel: LightModelId);
        var request = OpenCodeTestHelpers.CreateRequest(prompt: input.Prompt, useLightTier: true);

        var body = await SendAndReadMessageBodyAsync(ctx, request);

        AssertModelRef(body, "anthropic", "claude-haiku-4-5");
        Assert.Equal(input.Prompt, body.GetProperty("parts")[0].GetProperty("text").GetString());
    }

    /// <summary>
    /// A resumed call, by id or as the workspace's main conversation, keeps the route's model: the light flag is ignored.
    /// </summary>
    [Property(Arbitrary = [typeof(PromptStringArbitrary)], MaxTest = 20)]
    public async Task RequestBody_LightFlagOnResumedCall_CarriesTheRouteModel(PromptInput input, bool byExplicitId)
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext(model: RouteModel, lightModel: LightModelId);
        var request = byExplicitId
            ? OpenCodeTestHelpers.CreateRequest(prompt: input.Prompt, resumeSessionId: "explicit-session-abc", useLightTier: true)
            : OpenCodeTestHelpers.CreateRequest(prompt: input.Prompt, useResume: true, useLightTier: true);

        var body = await SendAndReadMessageBodyAsync(ctx, request, sessionExists: byExplicitId);

        AssertModelRef(body, "anthropic", "claude-sonnet-4-20250514");
    }

    [Fact]
    public async Task RequestBody_LightCall_UnsetLightModel_CarriesTheRouteModel()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext(model: RouteModel);

        var body = await SendAndReadMessageBodyAsync(ctx, OpenCodeTestHelpers.CreateRequest(useLightTier: true));

        AssertModelRef(body, "anthropic", "claude-sonnet-4-20250514");
    }

    [Fact]
    public async Task RequestBody_NoLightFlag_CarriesTheRouteModel_EvenWhenALightModelIsSet()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext(model: RouteModel, lightModel: LightModelId);

        var body = await SendAndReadMessageBodyAsync(ctx, OpenCodeTestHelpers.CreateRequest());

        AssertModelRef(body, "anthropic", "claude-sonnet-4-20250514");
    }

    [Fact]
    public async Task RequestBody_LightCall_LightModelAuto_LeavesTheModelToTheServer_NotTheRoutes()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext(model: RouteModel, lightModel: "auto");

        var body = await SendAndReadMessageBodyAsync(ctx, OpenCodeTestHelpers.CreateRequest(useLightTier: true));

        Assert.True(!body.TryGetProperty("model", out var model) || model.ValueKind == JsonValueKind.Null,
            "auto leaves the choice to the server, which differs from an unset light model");
    }

    [Fact]
    public void LightModel_IsExposed_AndNullWhenUnset()
    {
        Assert.Equal(LightModelId, OpenCodeTestHelpers.CreateTestContext(model: RouteModel, lightModel: LightModelId).Provider.LightModel);
        Assert.Null(OpenCodeTestHelpers.CreateTestContext(model: RouteModel).Provider.LightModel);
        Assert.Equal(RouteModel, OpenCodeTestHelpers.CreateTestContext(model: RouteModel, lightModel: LightModelId).Provider.Model);
    }

    // ── BuildTextPart helper ──────────────────────────────────────────────

    /// <summary>
    /// The text part is always built from the prompt — verified via a real execute call.
    /// </summary>
    [Fact]
    public async Task BuildTextPart_IncludesPromptText_InRequestBody()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-text-part");
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "response" }]
        });

        await ctx.Provider.EnsureSessionAsync(Path.GetTempPath(), CancellationToken.None);
        await ctx.Provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest("my unique prompt text"), CancellationToken.None);

        var messageRequest = ctx.Handler.Requests
            .FirstOrDefault(r => r.Path.Contains("/message"));
        messageRequest.Should().NotBeNull();
        messageRequest!.Body.Should().Contain("my unique prompt text");
    }
}

/// <summary>
/// Input model for the request body property test.
/// Contains a prompt string that may include special characters, unicode, or be empty.
/// </summary>
public sealed class PromptInput
{
    public required string Prompt { get; init; }

    public override string ToString()
    {
        var display = Prompt.Length > 50 ? Prompt[..50] + "..." : Prompt;
        return $"PromptInput(Length={Prompt.Length}, Preview=\"{display}\")";
    }
}

/// <summary>
/// FsCheck arbitrary generators for prompt string property tests.
/// Generates diverse prompt strings including special characters, unicode, empty strings,
/// and multi-line content to verify JSON serialization correctness.
/// </summary>
public static class PromptStringArbitrary
{
    /// <summary>
    /// Printable ASCII characters for basic prompt content.
    /// </summary>
    private static readonly char[] AsciiChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,;:!?-_=+()[]{}/<>@#$%^&*~`'\""
            .ToCharArray();

    /// <summary>
    /// Special characters that test JSON escaping (quotes, backslashes, control chars).
    /// </summary>
    private static readonly char[] SpecialChars =
        ['"', '\\', '/', '\n', '\r', '\t', '\b', '\f'];

    /// <summary>
    /// Unicode characters from various scripts to test encoding correctness.
    /// </summary>
    private static readonly string[] UnicodeStrings =
    [
        "こんにちは",    // Japanese
        "你好世界",      // Chinese
        "مرحبا",        // Arabic
        "Привет",       // Russian
        "🚀🎉💻",      // Emoji
        "café",         // Accented Latin
        "naïve",        // Diaeresis
        "Ñoño",         // Spanish
        "∑∏∫√",         // Math symbols
        "→←↑↓",         // Arrows
    ];

    /// <summary>
    /// Generates a basic ASCII prompt string.
    /// </summary>
    private static Gen<string> AsciiPromptGen()
    {
        return
            from len in Gen.Choose(1, 100)
            from chars in Gen.ArrayOf(Gen.Elements(AsciiChars), len)
            select new string(chars);
    }

    /// <summary>
    /// Generates a prompt with special characters that require JSON escaping.
    /// </summary>
    private static Gen<string> SpecialCharPromptGen()
    {
        var allChars = AsciiChars.Concat(SpecialChars).ToArray();
        return
            from len in Gen.Choose(1, 60)
            from chars in Gen.ArrayOf(Gen.Elements(allChars), len)
            select new string(chars);
    }

    /// <summary>
    /// Generates a prompt containing unicode characters.
    /// </summary>
    private static Gen<string> UnicodePromptGen()
    {
        return
            from prefix in AsciiPromptGen()
            from unicode in Gen.Elements(UnicodeStrings)
            from suffix in AsciiPromptGen()
            select $"{prefix} {unicode} {suffix}";
    }

    /// <summary>
    /// Generates a multi-line prompt (contains newlines).
    /// </summary>
    private static Gen<string> MultiLinePromptGen()
    {
        return
            from lineCount in Gen.Choose(2, 5)
            from lines in Gen.ArrayOf(AsciiPromptGen(), lineCount)
            select string.Join("\n", lines);
    }

    /// <summary>
    /// Generates an empty string prompt.
    /// </summary>
    private static Gen<string> EmptyPromptGen()
    {
        return Gen.Constant(string.Empty);
    }

    /// <summary>
    /// Generates diverse prompt strings covering ASCII, special chars, unicode,
    /// multi-line, and empty strings.
    /// </summary>
    public static Arbitrary<PromptInput> PromptInputArb()
    {
        var gen = Gen.Frequency(
            (4, AsciiPromptGen()),
            (3, SpecialCharPromptGen()),
            (2, UnicodePromptGen()),
            (2, MultiLinePromptGen()),
            (1, EmptyPromptGen())
        ).Select(prompt => new PromptInput { Prompt = prompt });

        return gen.ToArbitrary();
    }
}
