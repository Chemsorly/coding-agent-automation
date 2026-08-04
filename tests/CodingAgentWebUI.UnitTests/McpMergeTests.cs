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

        // TODO [WARNING]: BeSameAs asserts reference equality (implementation detail). If the
        // implementation is changed to return a copy (semantically equivalent), this test will fail.
        // Replace with result.Should().BeEquivalentTo(profile) to test observable behavior.
        result.Should().BeSameAs(profile);
    }

    [Fact]
    public void MergeMcpServers_WhenProjectIsEmpty_ReturnsProfileListUnchanged()
    {
        var profile = new[] { Stdio("context7"), Stdio("web-search") };

        var result = DispatchOrchestrationService.MergeMcpServers(profile, []);

        // TODO [WARNING]: BeSameAs asserts reference equality (implementation detail). If the
        // implementation is changed to return a copy (semantically equivalent), this test will fail.
        // Replace with result.Should().BeEquivalentTo(profile) to test observable behavior.
        result.Should().BeSameAs(profile);
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
    public void MergeMcpServers_Passthrough_NullProject_BothEmpty_ProfileMaintained()
    {
        var profile = new[] { Stdio("context7"), Stdio("web-search") };

        // null project => passthrough
        var result = DispatchOrchestrationService.MergeMcpServers(profile, null);

        // TODO [WARNING]: This test is a duplicate of MergeMcpServers_WhenProjectIsNull_ReturnProfileListUnchanged —
        // both test null project with a two-element profile. The name is also misleading ("BothEmpty" but profile is
        // not empty). Replace with a genuinely distinct case or remove.
        result.Should().BeSameAs(profile);
        result.Should().HaveCount(2);
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
    /// Determinism: same inputs always produce the same output (same names in same order).
    /// </summary>
    // TODO [WARNING]: This test only checks name sequence equality, not full config field equality.
    // A non-deterministic implementation that shuffled Disabled/Type/Command values within the same
    // name order would still pass. Consider comparing full server equality (Name + Disabled + Command)
    // to verify the correct config is returned, not just the correct ordering.
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(McpArbitraries) })]
    public bool MergeMcpServers_IsDeterministic(McpServerConfig[] profileServers, McpServerConfig[] projectServers)
    {
        var r1 = DispatchOrchestrationService.MergeMcpServers(profileServers, projectServers);
        var r2 = DispatchOrchestrationService.MergeMcpServers(profileServers, projectServers);
        return r1.Select(s => s.Name).SequenceEqual(r2.Select(s => s.Name));
    }

    /// <summary>
    /// Idempotency: applying the same project MCPs twice produces the same result as applying once.
    /// </summary>
    // TODO [WARNING]: McpArbitraries deduplicates inputs by name before the test runs, so the merged
    // output can never contain duplicate names. This means the idempotency invariant is trivially
    // satisfied for all generated inputs — a bug that produced duplicates in edge cases would not be
    // caught. Consider feeding the raw merged result back without pre-deduplication, or using a broader
    // generator that includes inputs with duplicate names.
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(McpArbitraries) })]
    public bool MergeMcpServers_IsIdempotent(McpServerConfig[] profileServers, McpServerConfig[] projectServers)
    {
        var once = DispatchOrchestrationService.MergeMcpServers(profileServers, projectServers).ToArray();
        var twice = DispatchOrchestrationService.MergeMcpServers(once, projectServers);
        return once.Select(s => s.Name).SequenceEqual(twice.Select(s => s.Name));
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
                    var seen = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in arr) seen[s.Name] = s;
                    return seen.Values.ToArray();
                }))
            .ToArbitrary();
    }
}
