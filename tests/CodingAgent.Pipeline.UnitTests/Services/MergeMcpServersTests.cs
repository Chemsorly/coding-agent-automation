using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="McpServerMerge.Merge"/>.
/// Covers: empty inputs, project-only, profile-only, override by name (case-insensitive),
/// additive merge (non-matching names), null inputs.
/// </summary>
public sealed class MergeMcpServersTests
{
    private static McpServerConfig S(string name, string cmd = "cmd") =>
        new() { Name = name, Type = "stdio", Command = cmd };

    // ── Empty inputs ──────────────────────────────────────────────────────

    [Fact]
    // TODO: This test only exercises the short-circuit passthrough path (empty projectServers).
    // The dictionary-merge code path is not covered here — the assertion passes even for a trivial
    // "return profileServers" implementation. Consider adding a test that exercises merge with a
    // non-empty projectServers list to provide signal about the merge path.
    public void Merge_BothEmpty_ReturnsEmpty()
    {
        McpServerMerge.Merge([], []).Should().BeEmpty();
    }

    [Fact]
    public void Merge_NullProject_ReturnsProfile()
    {
        var profile = new[] { S("m1") };
        var result = McpServerMerge.Merge(profile, null);
        result.Should().HaveCount(1);
        result[0].Name.Should().Be("m1");
    }

    [Fact]
    public void Merge_EmptyProject_ReturnsProfile()
    {
        var profile = new[] { S("m1"), S("m2") };
        var result = McpServerMerge.Merge(profile, []);
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Merge_EmptyProfile_ReturnsProject()
    {
        var project = new[] { S("p1") };
        var result = McpServerMerge.Merge([], project);
        result.Should().HaveCount(1);
        result[0].Name.Should().Be("p1");
    }

    // ── Override by name ──────────────────────────────────────────────────

    [Fact]
    public void Merge_ProjectOverridesProfileByName()
    {
        var profile = new[] { S("shared", "old-cmd") };
        var project = new[] { S("shared", "new-cmd") };

        var result = McpServerMerge.Merge(profile, project);

        result.Should().HaveCount(1);
        result[0].Command.Should().Be("new-cmd");
    }

    [Fact]
    public void Merge_OverrideIsCaseInsensitive()
    {
        var profile = new[] { S("Shared", "old") };
        var project = new[] { S("shared", "new") };

        var result = McpServerMerge.Merge(profile, project);

        // Should have only 1 entry (override), not 2 (additive)
        result.Should().HaveCount(1);
    }

    // ── Additive merge ────────────────────────────────────────────────────

    [Fact]
    public void Merge_NonMatchingNames_AreBothPresent()
    {
        var profile = new[] { S("profile-mcp") };
        var project = new[] { S("project-mcp") };

        var result = McpServerMerge.Merge(profile, project);

        result.Should().HaveCount(2);
        result.Select(s => s.Name).Should().Contain("profile-mcp").And.Contain("project-mcp");
    }

    [Fact]
    public void Merge_MixedOverrideAndAdditive()
    {
        var profile = new[] { S("shared", "old"), S("profile-only") };
        var project = new[] { S("shared", "new"), S("project-only") };

        var result = McpServerMerge.Merge(profile, project);

        result.Should().HaveCount(3); // shared (overridden), profile-only, project-only
        result.First(s => s.Name == "shared").Command.Should().Be("new");
    }

    // ── Profile items not in project are preserved ────────────────────────

    [Fact]
    public void Merge_ProfileItemsNotInProject_AreKept()
    {
        var profile = new[] { S("m1"), S("m2"), S("m3") };
        var project = new[] { S("m2", "new") }; // only overrides m2

        var result = McpServerMerge.Merge(profile, project);

        result.Should().HaveCount(3);
        result.Select(s => s.Name).Should().Contain("m1").And.Contain("m2").And.Contain("m3");
    }
}
