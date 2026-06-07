using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Property-based tests for response parts extraction (Property 2).
/// Verifies that OpenCodeAgentProvider correctly extracts only "text" type parts,
/// concatenates them with newlines, splits into lines, and preserves non-ANSI content.
/// Feature: opencode-agent-executor
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "2")]
public class OpenCodeResponsePartsPropertyTests
{
    /// <summary>
    /// Property 2: Response Parts Extraction
    /// For any valid JSON response containing a parts array with entries of varying types
    /// (text, tool_use, tool_result, etc.), the provider SHALL extract only entries where
    /// type === "text", concatenate their text values with newlines, split into individual lines,
    /// and populate AgentResult.OutputLines with the result. All non-ANSI content SHALL be
    /// preserved unchanged.
    /// **Validates: Requirements 1.4, 3.5**
    /// </summary>
    [Property(Arbitrary = [typeof(ResponsePartsArbitrary)], MaxTest = 20)]
    public async void OnlyTextPartsAreExtracted_AndConcatenatedWithNewlines(ResponsePartsInput input)
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        // Enqueue session creation response (consumed by POST /session)
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler);

        // Use URL-pattern matching for the message response to avoid FIFO race with SSE
        var messageResponse = new SendMessageResponse { Parts = input.Parts };
        ctx.Handler.ForUrlPattern("/session/.+/message", messageResponse);

        var request = OpenCodeTestHelpers.CreateRequest(prompt: "test");

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        // Assert: exit code should be success
        Assert.Equal(ExitCodes.Success, result.ExitCode);

        // Compute expected output: extract only text parts, concatenate with newlines, split
        var textParts = input.Parts
            .Where(p => string.Equals(p.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Text ?? string.Empty)
            .ToList();

        var combinedText = string.Join("\n", textParts);
        var expectedLines = combinedText.Split('\n').ToList();

        // The provider applies ANSI stripping, so we apply the same to expected
        var expectedStripped = expectedLines
            .Select(line => OpenCodeAgentProvider.StripAnsiEscapes(line))
            .ToList();

        Assert.Equal(expectedStripped.Count, result.OutputLines.Count);
        for (var i = 0; i < expectedStripped.Count; i++)
        {
            Assert.Equal(expectedStripped[i], result.OutputLines[i]);
        }
    }

    /// <summary>
    /// Property 2 (sub-property): Non-text parts are never included in output.
    /// For any response with ONLY non-text parts, OutputLines contains a single empty string.
    /// **Validates: Requirements 1.4, 3.5**
    /// </summary>
    [Property(Arbitrary = [typeof(ResponsePartsArbitrary)], MaxTest = 20)]
    public async void NonTextPartsAreExcludedFromOutput(NonTextOnlyInput input)
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler);

        // Use URL-pattern matching for the message response to avoid FIFO race with SSE
        var messageResponse = new SendMessageResponse { Parts = input.Parts };
        ctx.Handler.ForUrlPattern("/session/.+/message", messageResponse);

        var request = OpenCodeTestHelpers.CreateRequest(prompt: "test");

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        // Assert: exit code should be success
        Assert.Equal(ExitCodes.Success, result.ExitCode);

        // With no text parts, the concatenation is empty string, split produces [""]
        // (string.Join of empty list = "", "".Split('\n') = [""])
        Assert.Single(result.OutputLines);
        Assert.Equal(string.Empty, result.OutputLines[0]);
    }
}

/// <summary>
/// Input model for the response parts extraction property test.
/// Contains a parts array with a mix of text and non-text entries.
/// </summary>
public sealed class ResponsePartsInput
{
    public required IReadOnlyList<MessagePart> Parts { get; init; }

    public override string ToString()
    {
        var textCount = Parts.Count(p => string.Equals(p.Type, "text", StringComparison.OrdinalIgnoreCase));
        return $"ResponsePartsInput(Total={Parts.Count}, TextParts={textCount})";
    }
}

/// <summary>
/// Input model for the non-text-only property test.
/// Contains a parts array with ONLY non-text entries.
/// </summary>
public sealed class NonTextOnlyInput
{
    public required IReadOnlyList<MessagePart> Parts { get; init; }

    public override string ToString()
    {
        return $"NonTextOnlyInput(Parts={Parts.Count})";
    }
}

/// <summary>
/// FsCheck arbitrary generators for response parts property tests.
/// Generates realistic MessagePart arrays with varying types.
/// </summary>
public static class ResponsePartsArbitrary
{
    /// <summary>
    /// Non-text part types that the OpenCode API may return.
    /// </summary>
    private static readonly string[] NonTextTypes = ["tool_use", "tool_result", "image", "thinking"];

    /// <summary>
    /// Characters safe for text content — printable ASCII without ANSI escape triggers.
    /// </summary>
    private static readonly char[] SafeChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,;:!?-_=+()[]{}/<>@#$%^&*~`'\""
            .ToCharArray();

    /// <summary>
    /// Generates safe text content without ANSI escape sequences.
    /// Uses printable ASCII characters to ensure the "non-ANSI content preserved unchanged" property is testable.
    /// </summary>
    private static Gen<string> SafeTextGen()
    {
        return
            from len in Gen.Choose(0, 80)
            from chars in Gen.ArrayOf(Gen.Elements(SafeChars), len)
            select new string(chars);
    }

    /// <summary>
    /// Generates text content that may contain newlines (multi-line text parts).
    /// </summary>
    private static Gen<string> MultiLineTextGen()
    {
        return
            from lineCount in Gen.Choose(1, 4)
            from lines in Gen.ArrayOf(SafeTextGen(), lineCount)
            select string.Join("\n", lines);
    }

    /// <summary>
    /// Generates a text-type MessagePart with safe content.
    /// </summary>
    private static Gen<MessagePart> TextPartGen()
    {
        return
            from text in MultiLineTextGen()
            select new MessagePart { Type = "text", Text = text };
    }

    /// <summary>
    /// Generates a non-text MessagePart (tool_use, tool_result, etc.).
    /// </summary>
    private static Gen<MessagePart> NonTextPartGen()
    {
        return
            from type in Gen.Elements(NonTextTypes)
            from text in SafeTextGen()
            select new MessagePart { Type = type, Text = text };
    }

    /// <summary>
    /// Generates a mixed array of MessageParts with at least one text part.
    /// Parts are interleaved (text, non-text, text, non-text, ...) to test ordering.
    /// </summary>
    public static Arbitrary<ResponsePartsInput> ResponsePartsInputArb()
    {
        var gen =
            from textCount in Gen.Choose(1, 5)
            from nonTextCount in Gen.Choose(0, 5)
            from textParts in Gen.ArrayOf(TextPartGen(), textCount)
            from nonTextParts in Gen.ArrayOf(NonTextPartGen(), nonTextCount)
            let allParts = InterleaveParts(textParts, nonTextParts)
            select new ResponsePartsInput { Parts = allParts };

        return gen.ToArbitrary();
    }

    /// <summary>
    /// Generates an array of ONLY non-text MessageParts (1-5 entries).
    /// </summary>
    public static Arbitrary<NonTextOnlyInput> NonTextOnlyInputArb()
    {
        var gen =
            from count in Gen.Choose(1, 5)
            from parts in Gen.ArrayOf(NonTextPartGen(), count)
            select new NonTextOnlyInput { Parts = parts.ToList() };

        return gen.ToArbitrary();
    }

    /// <summary>
    /// Interleaves text and non-text parts to create a realistic mixed array.
    /// Alternates between the two arrays, appending remaining items at the end.
    /// </summary>
    private static IReadOnlyList<MessagePart> InterleaveParts(MessagePart[] textParts, MessagePart[] nonTextParts)
    {
        var result = new List<MessagePart>(textParts.Length + nonTextParts.Length);
        var maxLen = Math.Max(textParts.Length, nonTextParts.Length);

        for (var i = 0; i < maxLen; i++)
        {
            if (i < textParts.Length)
                result.Add(textParts[i]);
            if (i < nonTextParts.Length)
                result.Add(nonTextParts[i]);
        }

        return result;
    }
}
