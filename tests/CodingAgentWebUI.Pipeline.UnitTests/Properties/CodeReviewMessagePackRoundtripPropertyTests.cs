using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using MessagePack;
using MessagePack.Resolvers;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// MessagePack serialization round-trip property tests for DTOs not covered by
/// HubMessageRoundtripPropertyTests or AdditionalMessagePackRoundtripPropertyTests.
/// Covers: AgentProfile, CodeReviewConfiguration, InlineCommentSettings.
/// Uses ContractlessStandardResolverAllowPrivate to match production SignalR config.
/// </summary>
public class CodeReviewMessagePackRoundtripPropertyTests
{
    private static readonly MessagePackSerializerOptions MsgPackOptions =
        ContractlessStandardResolverAllowPrivate.Options;

    private static T RoundTrip<T>(T original)
    {
        var bytes = MessagePackSerializer.Serialize(original, MsgPackOptions);
        return MessagePackSerializer.Deserialize<T>(bytes, MsgPackOptions);
    }

    // ── AgentProfile ─────────────────────────────────────────────────────

    [Property(MaxTest = 20)]
    public Property AgentProfile_RoundTrip_PreservesAllFields()
    {
        var mcpServerGen =
            from name in Gen.Elements("context7", "web-search", "github-mcp", "sequential-thinking")
            from type in Gen.Elements("stdio", "http")
            from command in Gen.Elements("uvx", "npx", "node")
            from hasUrl in Gen.Elements(true, false)
            from disabled in Gen.Elements(true, false)
            from argCount in Gen.Choose(0, 3)
            from args in Gen.ListOf(Gen.Elements("-y", "mcp-server@latest", "--port=3000"))
            select new McpServerConfig
            {
                Name = name,
                Type = type,
                Command = type == "stdio" ? command : null,
                Args = type == "stdio" ? args.Take(argCount).ToList() : [],
                Url = type == "http" && hasUrl ? "https://mcp.example.com/mcp" : null,
                Env = new Dictionary<string, string> { ["LOG_LEVEL"] = "ERROR" },
                Disabled = disabled,
                AutoApprove = []
            };

        var gen =
            from displayName in Gen.Elements("Default Agent", "Review Bot", "Code Generator")
            from agentProviderConfigId in Gen.Elements("apc-001", "apc-002", "apc-003")
            from enabled in Gen.Elements(true, false)
            from priority in Gen.Choose(0, 10)
            from labelCount in Gen.Choose(0, 4)
            from labels in Gen.ListOf(Gen.Elements("dotnet", "python", "kiro", "linux", "windows"))
            from mcpCount in Gen.Choose(0, 3)
            from mcpServers in Gen.ListOf(mcpServerGen)
            select new AgentProfile
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = displayName,
                MatchLabels = labels.Take(labelCount).ToList(),
                AgentProviderConfigId = agentProviderConfigId,
                Enabled = enabled,
                Priority = priority,
                McpServers = mcpServers.Take(mcpCount).ToList()
            };

        return Prop.ForAll(gen.ToArbitrary(), original =>
        {
            var deserialized = RoundTrip(original);

            deserialized.Id.Should().Be(original.Id);
            deserialized.DisplayName.Should().Be(original.DisplayName);
            deserialized.MatchLabels.Should().BeEquivalentTo(original.MatchLabels);
            deserialized.AgentProviderConfigId.Should().Be(original.AgentProviderConfigId);
            deserialized.Enabled.Should().Be(original.Enabled);
            deserialized.Priority.Should().Be(original.Priority);
            deserialized.McpServers.Should().HaveCount(original.McpServers.Count);

            for (var i = 0; i < original.McpServers.Count; i++)
            {
                var orig = original.McpServers[i];
                var deser = deserialized.McpServers[i];
                deser.Name.Should().Be(orig.Name);
                deser.Type.Should().Be(orig.Type);
                deser.Command.Should().Be(orig.Command);
                deser.Args.Should().BeEquivalentTo(orig.Args);
                deser.Url.Should().Be(orig.Url);
                deser.Disabled.Should().Be(orig.Disabled);
            }
        });
    }

    // ── InlineCommentSettings ────────────────────────────────────────────

    [Property(MaxTest = 20)]
    public Property InlineCommentSettings_RoundTrip_PreservesAllFields()
    {
        var gen =
            from enabled in Gen.Elements(true, false)
            from maxComments in Gen.Choose(1, 50)
            from maxRetries in Gen.Choose(0, 5)
            from orderBySeverity in Gen.Elements(true, false)
            from severity in Gen.Elements(
                FindingSeverity.Suggestion,
                FindingSeverity.Warning,
                FindingSeverity.Critical)
            select new InlineCommentSettings
            {
                Enabled = enabled,
                MaxInlineComments = maxComments,
                MaxRetries = maxRetries,
                OrderBySeverity = orderBySeverity,
                SeverityThreshold = severity
            };

        return Prop.ForAll(gen.ToArbitrary(), original =>
        {
            var deserialized = RoundTrip(original);

            deserialized.Enabled.Should().Be(original.Enabled);
            deserialized.MaxInlineComments.Should().Be(original.MaxInlineComments);
            deserialized.MaxRetries.Should().Be(original.MaxRetries);
            deserialized.OrderBySeverity.Should().Be(original.OrderBySeverity);
            deserialized.SeverityThreshold.Should().Be(original.SeverityThreshold);
        });
    }

    // ── CodeReviewConfiguration ──────────────────────────────────────────

    [Property(MaxTest = 20)]
    public Property CodeReviewConfiguration_RoundTrip_PreservesAllFields()
    {
        var inlineSettingsGen =
            from enabled in Gen.Elements(true, false)
            from maxComments in Gen.Choose(1, 50)
            from maxRetries in Gen.Choose(0, 5)
            from orderBySeverity in Gen.Elements(true, false)
            from severity in Gen.Elements(
                FindingSeverity.Suggestion,
                FindingSeverity.Warning,
                FindingSeverity.Critical)
            select new InlineCommentSettings
            {
                Enabled = enabled,
                MaxInlineComments = maxComments,
                MaxRetries = maxRetries,
                OrderBySeverity = orderBySeverity,
                SeverityThreshold = severity
            };

        var gen =
            from hasFixPrompt in Gen.Elements(true, false)
            from fixPrompt in Gen.Elements(
                "Fix all CRITICAL findings in place",
                "Apply the suggested fix for each finding",
                "Rewrite the affected code section")
            from maxIterations in Gen.Choose(1, 5)
            from inlineComments in inlineSettingsGen
            from reviewIsolation in Gen.Elements(ReviewIsolation.Shared, ReviewIsolation.Isolated)
            select new CodeReviewConfiguration
            {
                FixPrompt = hasFixPrompt ? fixPrompt : null,
                InlineComments = inlineComments,
                MaxIterations = maxIterations,
                ReviewIsolation = reviewIsolation
            };

        return Prop.ForAll(gen.ToArbitrary(), original =>
        {
            var deserialized = RoundTrip(original);

            deserialized.FixPrompt.Should().Be(original.FixPrompt);
            deserialized.MaxIterations.Should().Be(original.MaxIterations);
            deserialized.ReviewIsolation.Should().Be(original.ReviewIsolation);

            // Nested InlineCommentSettings
            deserialized.InlineComments.Enabled.Should().Be(original.InlineComments.Enabled);
            deserialized.InlineComments.MaxInlineComments.Should().Be(original.InlineComments.MaxInlineComments);
            deserialized.InlineComments.MaxRetries.Should().Be(original.InlineComments.MaxRetries);
            deserialized.InlineComments.OrderBySeverity.Should().Be(original.InlineComments.OrderBySeverity);
            deserialized.InlineComments.SeverityThreshold.Should().Be(original.InlineComments.SeverityThreshold);
        });
    }
}
