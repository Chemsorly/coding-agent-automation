using System.Net;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Property-based tests for FileDiff to FileChangeSummary mapping (Property 9).
/// Verifies that OpenCodeAgentProvider.GetSessionDiffAsync correctly maps FileDiff status
/// values to FileChangeSummary status strings, preserves line counts, and returns empty
/// list on any failure.
/// Feature: opencode-agent-executor
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "9")]
public class OpenCodeDiffMappingPropertyTests
{
    /// <summary>
    /// Property 9: FileDiff to FileChangeSummary Mapping — Status Mapping
    /// For any FileDiff[] response, each entry SHALL be mapped to a FileChangeSummary with:
    /// status "Added" for status === "added", status "Deleted" for status === "deleted",
    /// status "Modified" for all others.
    /// **Validates: Requirements 7.3, 7.4, 7.5**
    /// </summary>
    [Property(Arbitrary = [typeof(DiffMappingArbitrary)], MaxTest = 100)]
    public async void StatusMappingIsCorrect_AddedDeletedOrModified(DiffMappingInput input)
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        // Enqueue session creation so _currentSessionId is set
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "diff-session-001");
        await ctx.Provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        // Enqueue the diff response using URL pattern to match GET /session/:id/diff
        var json = JsonSerializer.Serialize(input.Diffs, OpenCodeJson.JsonOptions);
        ctx.Handler.ForUrlPattern("/session/.+/diff", HttpStatusCode.OK, json);

        // Act
        var result = await ctx.Provider.GetSessionDiffAsync(CancellationToken.None);

        // Assert: same count
        Assert.Equal(input.Diffs.Count, result.Count);

        // Assert: each entry is correctly mapped
        for (var i = 0; i < input.Diffs.Count; i++)
        {
            var diff = input.Diffs[i];
            var summary = result[i];

            // Status mapping
            var expectedStatus = diff.Status?.ToLowerInvariant() switch
            {
                "added" => "Added",
                "deleted" => "Deleted",
                _ => "Modified"
            };

            Assert.Equal(expectedStatus, summary.Status);
            Assert.Equal(diff.Path, summary.Path);
            Assert.Equal(diff.LinesAdded, summary.LinesAdded);
            Assert.Equal(diff.LinesDeleted, summary.LinesDeleted);
        }
    }

    /// <summary>
    /// Property 9: FileDiff to FileChangeSummary Mapping — Line Counts Preserved
    /// For any FileDiff[], the LinesAdded and LinesDeleted values SHALL be preserved
    /// exactly in the resulting FileChangeSummary.
    /// **Validates: Requirements 7.3, 7.4, 7.5**
    /// </summary>
    [Property(Arbitrary = [typeof(DiffMappingArbitrary)], MaxTest = 100)]
    public async void LineCounts_ArePreservedExactly(DiffMappingInput input)
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "diff-session-002");
        await ctx.Provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        var json = JsonSerializer.Serialize(input.Diffs, OpenCodeJson.JsonOptions);
        ctx.Handler.ForUrlPattern("/session/.+/diff", HttpStatusCode.OK, json);

        // Act
        var result = await ctx.Provider.GetSessionDiffAsync(CancellationToken.None);

        // Assert
        Assert.Equal(input.Diffs.Count, result.Count);
        for (var i = 0; i < input.Diffs.Count; i++)
        {
            Assert.Equal(input.Diffs[i].LinesAdded, result[i].LinesAdded);
            Assert.Equal(input.Diffs[i].LinesDeleted, result[i].LinesDeleted);
        }
    }

    /// <summary>
    /// Property 9: FileDiff to FileChangeSummary Mapping — HTTP Error Returns Empty List
    /// On any HTTP error (4xx, 5xx), an empty list SHALL be returned without throwing.
    /// **Validates: Requirements 7.3, 7.4, 7.5**
    /// </summary>
    [Property(Arbitrary = [typeof(DiffMappingArbitrary)], MaxTest = 100)]
    public async void HttpError_ReturnsEmptyList_WithoutThrowing(HttpErrorInput input)
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "diff-session-003");
        await ctx.Provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        // Enqueue an error response for the diff endpoint
        ctx.Handler.ForUrlPattern("/session/.+/diff", input.StatusCode, "{\"error\":\"test failure\"}");

        // Act
        var result = await ctx.Provider.GetSessionDiffAsync(CancellationToken.None);

        // Assert: empty list, no exception
        Assert.Empty(result);
    }

    /// <summary>
    /// Property 9: FileDiff to FileChangeSummary Mapping — Empty Diff Array
    /// When the diff endpoint returns an empty array, an empty list SHALL be returned.
    /// **Validates: Requirements 7.3, 7.4, 7.5**
    /// </summary>
    [Fact]
    public async Task EmptyDiffArray_ReturnsEmptyList()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "diff-session-004");
        await ctx.Provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        ctx.Handler.ForUrlPattern("/session/.+/diff", HttpStatusCode.OK, "[]");

        // Act
        var result = await ctx.Provider.GetSessionDiffAsync(CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    /// <summary>
    /// Property 9: No session returns empty list without throwing.
    /// **Validates: Requirements 7.3, 7.4, 7.5**
    /// </summary>
    [Fact]
    public async Task NoSession_ReturnsEmptyList()
    {
        // Arrange — don't call EnsureSessionAsync, so _currentSessionId is null
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        // Act
        var result = await ctx.Provider.GetSessionDiffAsync(CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }
}

/// <summary>
/// Input model for the diff mapping property test.
/// Contains a list of FileDiff entries with varying statuses and line counts.
/// </summary>
public sealed class DiffMappingInput
{
    public required IReadOnlyList<FileDiff> Diffs { get; init; }

    public override string ToString()
    {
        var added = Diffs.Count(d => string.Equals(d.Status, "added", StringComparison.OrdinalIgnoreCase));
        var deleted = Diffs.Count(d => string.Equals(d.Status, "deleted", StringComparison.OrdinalIgnoreCase));
        var other = Diffs.Count - added - deleted;
        return $"DiffMappingInput(Total={Diffs.Count}, Added={added}, Deleted={deleted}, Modified={other})";
    }
}

/// <summary>
/// Input model for the HTTP error property test.
/// Contains an HTTP error status code.
/// </summary>
public sealed class HttpErrorInput
{
    public required HttpStatusCode StatusCode { get; init; }

    public override string ToString() => $"HttpErrorInput(StatusCode={(int)StatusCode})";
}

/// <summary>
/// FsCheck arbitrary generators for diff mapping property tests.
/// Generates realistic FileDiff arrays with varying statuses and line counts.
/// </summary>
public static class DiffMappingArbitrary
{
    /// <summary>
    /// Known status values that map to specific FileChangeSummary statuses.
    /// </summary>
    private static readonly string[] KnownStatuses = ["added", "deleted", "modified"];

    /// <summary>
    /// Additional status values that should all map to "Modified".
    /// </summary>
    private static readonly string[] OtherStatuses = ["renamed", "copied", "changed", "unknown", "moved", ""];

    /// <summary>
    /// Characters safe for file paths.
    /// </summary>
    private static readonly char[] PathChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_./"
            .ToCharArray();

    /// <summary>
    /// Generates a random file path.
    /// </summary>
    private static Gen<string> FilePathGen()
    {
        return
            from segmentCount in Gen.Choose(1, 4)
            from segments in Gen.ArrayOf(
                from len in Gen.Choose(1, 15)
                from chars in Gen.ArrayOf(Gen.Elements(PathChars.Where(c => c != '/').ToArray()), len)
                select new string(chars),
                segmentCount)
            select string.Join("/", segments);
    }

    /// <summary>
    /// Generates a random diff status — includes "added", "deleted", and various other values
    /// that should all map to "Modified".
    /// </summary>
    private static Gen<string?> StatusGen()
    {
        var allStatuses = KnownStatuses.Concat(OtherStatuses).ToArray();
        return Gen.Frequency(
            (3, Gen.Elements(allStatuses).Select(s => (string?)s)),
            (1, Gen.Constant((string?)null)));
    }

    /// <summary>
    /// Generates a single FileDiff with random status and line counts.
    /// </summary>
    private static Gen<FileDiff> FileDiffGen()
    {
        return
            from path in FilePathGen()
            from status in StatusGen()
            from linesAdded in Gen.Choose(0, 500)
            from linesDeleted in Gen.Choose(0, 500)
            select new FileDiff
            {
                Path = path,
                Status = status,
                LinesAdded = linesAdded,
                LinesDeleted = linesDeleted
            };
    }

    /// <summary>
    /// Generates a DiffMappingInput with 1-10 FileDiff entries.
    /// </summary>
    public static Arbitrary<DiffMappingInput> DiffMappingInputArb()
    {
        var gen =
            from count in Gen.Choose(1, 10)
            from diffs in Gen.ArrayOf(FileDiffGen(), count)
            select new DiffMappingInput { Diffs = diffs.ToList() };

        return gen.ToArbitrary();
    }

    /// <summary>
    /// HTTP error status codes (4xx and 5xx).
    /// </summary>
    private static readonly HttpStatusCode[] ErrorStatusCodes =
    [
        HttpStatusCode.BadRequest,
        HttpStatusCode.Unauthorized,
        HttpStatusCode.Forbidden,
        HttpStatusCode.NotFound,
        HttpStatusCode.MethodNotAllowed,
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout
    ];

    /// <summary>
    /// Generates an HttpErrorInput with a random error status code.
    /// </summary>
    public static Arbitrary<HttpErrorInput> HttpErrorInputArb()
    {
        var gen = Gen.Elements(ErrorStatusCodes)
            .Select(code => new HttpErrorInput { StatusCode = code });

        return gen.ToArbitrary();
    }
}
