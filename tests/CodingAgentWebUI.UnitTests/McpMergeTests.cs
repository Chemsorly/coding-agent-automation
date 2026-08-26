using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
// ReSharper disable InconsistentNaming

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit and property-based tests for <see cref="DispatchOrchestrationService.MergeMcpServers"/>.
/// Verifies override, additive, passthrough, case-insensitivity, and determinism/idempotency.
/// </summary>
public class McpMergeTests
{
    // ── Helper ───────────────────────────────────────────────────────────────

    private static McpServerConfig Stdio(string name, bool disabled = false) => new()
    {
        Name = name,
        Type = "stdio",
        Command = "uvx",
        Args = [],
        Disabled = disabled
    };

    // ── Unit tests ───────────────────────────────────────────────────────────

    [Fact]
    public void MergeMcpServers_WhenProjectIsNull_ReturnProfileListUnchanged()
    {
        var profile = new[] { Stdio("context7"), Stdio("web-search") };

        var result = DispatchOrchestrationService.MergeMcpServers(profile, null);

        result.Should().BeEquivalentTo(profile, opts => opts.WithStrictOrdering(),
            "null project must pass profile through unchanged");
    }

    [Fact]
    public void MergeMcpServers_WhenProjectIsEmpty_ReturnsProfileListUnchanged()
    {
        var profile = new[] { Stdio("context7"), Stdio("web-search") };

        var result = DispatchOrchestrationService.MergeMcpServers(profile, []);

        result.Should().BeEquivalentTo(profile, opts => opts.WithStrictOrdering(),
            "empty project list must pass profile through unchanged");
    }

    [Fact]
    public void MergeMcpServers_WhenBothEmpty_ReturnsEmpty()
    {
        var result = DispatchOrchestrationService.MergeMcpServers([], null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void MergeMcpServers_Override_ProjectServerReplacesProfileServerWithSameName()
    {
        var profile = new[] { Stdio("context7"), Stdio("web-search") };
        var project = new[] { Stdio("web-search", disabled: true) };

        var result = DispatchOrchestrationService.MergeMcpServers(profile, project);

        result.Should().HaveCount(2);
        result.Should().Contain(s => s.Name == "context7" && !s.Disabled);
        result.Should().Contain(s => s.Name == "web-search" && s.Disabled);
    }

    [Fact]
    public void MergeMcpServers_Additive_ProjectServerWithNewNameIsAppended()
    {
        var profile = new[] { Stdio("context7") };
        var project = new[] { Stdio("sonarqube-mcp") };

        var result = DispatchOrchestrationService.MergeMcpServers(profile, project);

        result.Should().HaveCount(2);
        result.Select(s => s.Name).Should().Contain("context7");
        result.Select(s => s.Name).Should().Contain("sonarqube-mcp");
    }

    [Fact]
    public void MergeMcpServers_CaseInsensitiveName_ProjectWinsNoDuplicate()
    {
        var profile = new[] { Stdio("Context7") };
        var project = new[] { Stdio("context7", disabled: true) };

        var result = DispatchOrchestrationService.MergeMcpServers(profile, project);

        result.Should().HaveCount(1);
        result[0].Disabled.Should().BeTrue("project server wins on collision");
    }

    [Fact]
    public void MergeMcpServers_ProfileEmpty_ProjectNonNull_ReturnsProjectServers()
    {
        var project = new[] { Stdio("custom") };

        var result = DispatchOrchestrationService.MergeMcpServers([], project);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("custom");
    }

    [Fact]
    public void MergeMcpServers_OverrideAndAdd_ProducesCorrectMerge()
    {
        // Profile: { context7, web-search }
        // Project: { web-search (disabled), sonarqube-mcp }
        // Expected: { context7 (enabled), web-search (disabled), sonarqube-mcp (enabled) }
        var profile = new[] { Stdio("context7"), Stdio("web-search") };
        var project = new[] { Stdio("web-search", disabled: true), Stdio("sonarqube-mcp") };

        var result = DispatchOrchestrationService.MergeMcpServers(profile, project);

        result.Should().HaveCount(3);
        result.Should().Contain(s => s.Name == "context7" && !s.Disabled);
        result.Should().Contain(s => s.Name == "web-search" && s.Disabled);
        result.Should().Contain(s => s.Name == "sonarqube-mcp" && !s.Disabled);
    }

    // ── Property-based tests ─────────────────────────────────────────────────

    /// <summary>
    /// Determinism: same inputs always produce the same output — same servers with the same field values.
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(McpArbitraries) })]
    public bool MergeMcpServers_IsDeterministic(McpServerConfig[] profileServers, McpServerConfig[] projectServers)
    {
        var r1 = DispatchOrchestrationService.MergeMcpServers(profileServers, projectServers);
        var r2 = DispatchOrchestrationService.MergeMcpServers(profileServers, projectServers);
        return r1.SequenceEqual(r2, McpServerConfigComparer.Instance);
    }

    /// <summary>
    /// Idempotency: applying the same project MCPs twice produces the same result as applying once.
    /// Feeding <paramref name="once"/> back as the profile ensures the shared-name override path
    /// is always exercised in the second call. <paramref name="projectServers"/> uses
    /// <see cref="NonEmptyMcpServerConfigArray"/> to guarantee it is always non-empty, so the
    /// shared-name override path via the feed-back is unconditionally exercised on every test run.
    /// </summary>
    // TODO [WARNING]: When profileServers is empty (possible with McpServerConfigArrayArb, size 0–4),
    // `once` consists only of projectServers entries, so the second call always finds every name in
    // `once` present in `projectServers.Value`. The additive path (a profile server with no match in
    // projectServers) is never exercised for those inputs, making the invariant trivially satisfied on
    // roughly one-fifth of generated inputs. Consider using a non-empty generator for profileServers
    // as well, or asserting that profile-only servers survive both calls.
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(McpArbitraries) })]
    public bool MergeMcpServers_IsIdempotent(McpServerConfig[] profileServers, NonEmptyMcpServerConfigArray projectServers)
    {
        var once = DispatchOrchestrationService.MergeMcpServers(profileServers, projectServers.Value).ToArray();
        var twice = DispatchOrchestrationService.MergeMcpServers(once, projectServers.Value);
        return once.SequenceEqual(twice, McpServerConfigComparer.Instance);
    }

    /// <summary>
    /// Compares <see cref="McpServerConfig"/> instances by the four scalar fields that define
    /// server identity and configuration for merge purposes: Name (case-insensitive), Disabled,
    /// Type, and Command.
    /// </summary>
    private sealed class McpServerConfigComparer : IEqualityComparer<McpServerConfig>
    {
        public static readonly McpServerConfigComparer Instance = new();

        public bool Equals(McpServerConfig? x, McpServerConfig? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)
                && x.Disabled == y.Disabled
                && x.Type == y.Type
                && x.Command == y.Command;
        }

        public int GetHashCode(McpServerConfig obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name ?? string.Empty),
                obj.Disabled,
                obj.Type,
                obj.Command);
    }
}

/// <summary>
/// FsCheck arbitrary generators for MCP merge property tests.
/// Produces McpServerConfig arrays with non-null, non-empty names.
/// </summary>
public static class McpArbitraries
{
    private static readonly string[] KnownNames = ["context7", "web-search", "sonarqube-mcp", "custom", "github-mcp", "linear-mcp"];

    public static Arbitrary<McpServerConfig[]> McpServerConfigArrayArb()
    {
        var configGen = Gen.Elements(KnownNames)
            .SelectMany(name => Gen.Elements(false, true)
                .Select(disabled => new McpServerConfig
                {
                    Name = name,
                    Type = "stdio",
                    Command = "uvx",
                    Args = [],
                    Disabled = disabled
                }));

        return Gen.Choose(0, 4)
            .SelectMany(count => Gen.ArrayOf(configGen, count)
                .Select(arr =>
                {
                    // Deduplicate by name (keep last) to produce valid input arrays
                    // TODO [WARNING]: This generator deduplicates within each individual array but does
                    // not prevent overlap in names *across* the two independently generated arrays
                    // (profileServers, projectServers). Future property tests that use McpArbitraries
                    // and rely on profile/project name overlap should be aware that such overlap is
                    // possible but not guaranteed (size 0 is included via Gen.Choose(0, 4)).
                    // For tests that require a non-empty projectServers, use NonEmptyMcpServerConfigArray.
                    var seen = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in arr) seen[s.Name] = s;
                    return seen.Values.ToArray();
                }))
            .ToArbitrary();
    }

    public static Arbitrary<NonEmptyMcpServerConfigArray> NonEmptyMcpServerConfigArrayArb()
    {
        var configGen = Gen.Elements(KnownNames)
            .SelectMany(name => Gen.Elements(false, true)
                .Select(disabled => new McpServerConfig
                {
                    Name = name,
                    Type = "stdio",
                    Command = "uvx",
                    Args = [],
                    Disabled = disabled
                }));

        return Gen.Choose(1, 4)
            .SelectMany(count => Gen.ArrayOf(configGen, count)
                .Select(arr =>
                {
                    var seen = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in arr) seen[s.Name] = s;
                    return new NonEmptyMcpServerConfigArray(seen.Values.ToArray());
                }))
            .ToArbitrary();
    }
}

/// <summary>
/// Wrapper for a non-empty <see cref="McpServerConfig"/> array, used with
/// <see cref="McpArbitraries.NonEmptyMcpServerConfigArrayArb"/> to guarantee property tests
/// always receive at least one project server (ensuring the override path is exercised).
/// </summary>
public sealed class NonEmptyMcpServerConfigArray(McpServerConfig[] value)
{
    public McpServerConfig[] Value { get; } = value;
}
