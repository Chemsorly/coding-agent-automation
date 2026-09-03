using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using MessagePack;
using MessagePack.Resolvers;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// MessagePack serialization round-trip property tests for four configuration DTOs that
/// are embedded in <see cref="JobAssignmentMessage"/> and therefore transmitted over SignalR.
/// A field added with the wrong <see cref="KeyAttribute"/> index would silently lose data
/// across the wire; these properties catch such regressions.
///
/// Covers: <see cref="QualityGateConfiguration"/>, <see cref="ReviewerConfiguration"/> +
/// <see cref="ReviewAgent"/>, <see cref="ProviderConfig"/> (including Settings dictionary
/// which is a historic serialization edge case), and <see cref="PipelineJobTemplate"/>.
/// </summary>
public class ConfigMessagePackRoundtripPropertyTests
{
    private static readonly string[] CompilationArgs = ["build", "--no-restore"];
    private static readonly string[] TestArgs = ["--no-build", "--logger", "trx"];
    private static readonly string[] BlacklistPaths = ["node_modules/", ".git/"];
    private static readonly string[] RequiredLabels = ["dotnet", "ci"];

    // Match production SignalR configuration
    private static readonly MessagePackSerializerOptions MsgPackOptions =
        ContractlessStandardResolverAllowPrivate.Options;

    private static T RoundTrip<T>(T original)
    {
        var bytes = MessagePackSerializer.Serialize(original, MsgPackOptions);
        return MessagePackSerializer.Deserialize<T>(bytes, MsgPackOptions);
    }

    // ── QualityGateConfiguration ──────────────────────────────────────────

    [Property(MaxTest = 20)]
    public Property QualityGateConfiguration_RoundTrip_PreservesAllFields()
    {
        var gen =
            from id in Gen.Elements("qgc-001", "qgc-002", "qgc-003")
            from displayName in Gen.Elements("dotnet build", "pytest", "mvn test")
            from labelCount in Gen.Choose(0, 3)
            from labels in Gen.ListOf(Gen.Elements("dotnet", "python", "java", "ci"))
            from compilationCmd in Gen.Elements("dotnet", "mvn", null as string)
            from testCmd in Gen.Elements("dotnet test", "pytest", null as string)
            from enabled in Gen.Elements(true, false)
            from order in Gen.Choose(0, 5)
            select new QualityGateConfiguration
            {
                Id = id,
                DisplayName = displayName,
                MatchLabels = labels.Take(labelCount).ToList(),
                CompilationCommand = compilationCmd,
                CompilationArguments = compilationCmd != null ? CompilationArgs : null,
                TestCommand = testCmd,
                TestArguments = testCmd != null ? TestArgs : null,
                Enabled = enabled,
                ExecutionOrder = order
            };

        return Prop.ForAll(gen.ToArbitrary(), original =>
        {
            var d = RoundTrip(original);

            d.Id.Should().Be(original.Id);
            d.DisplayName.Should().Be(original.DisplayName);
            d.MatchLabels.Should().BeEquivalentTo(original.MatchLabels);
            d.CompilationCommand.Should().Be(original.CompilationCommand);
            d.CompilationArguments.Should().BeEquivalentTo(original.CompilationArguments);
            d.TestCommand.Should().Be(original.TestCommand);
            d.TestArguments.Should().BeEquivalentTo(original.TestArguments);
            d.Enabled.Should().Be(original.Enabled);
            d.ExecutionOrder.Should().Be(original.ExecutionOrder);
        });
    }

    // ── ReviewerConfiguration + ReviewAgent ──────────────────────────────

    [Property(MaxTest = 20)]
    public Property ReviewerConfiguration_RoundTrip_PreservesAllFields()
    {
        var agentGen =
            from name in Gen.Elements("SecurityBot", "StyleBot", "ArchBot", "PerformanceBot")
            from prompt in Gen.Elements(
                "Review for security issues.",
                "Check code style and naming conventions.",
                "Evaluate architectural decisions.",
                "Identify performance bottlenecks.")
            select new ReviewAgent { Name = name, Prompt = prompt };

        var gen =
            from id in Gen.Elements("rc-001", "rc-002")
            from displayName in Gen.Elements("Security Review", "Style Review", "Full Review")
            from labelCount in Gen.Choose(0, 3)
            from labels in Gen.ListOf(Gen.Elements("dotnet", "security", "all", "python"))
            from agentCount in Gen.Choose(1, 3)
            from agents in Gen.ListOf(agentGen)
            from enabled in Gen.Elements(true, false)
            from order in Gen.Choose(0, 10)
            select new ReviewerConfiguration
            {
                Id = id,
                DisplayName = displayName,
                MatchLabels = labels.Take(labelCount).ToList(),
                Agents = agents.Take(agentCount).ToList(),
                Enabled = enabled,
                ExecutionOrder = order
            };

        return Prop.ForAll(gen.ToArbitrary(), original =>
        {
            var d = RoundTrip(original);

            d.Id.Should().Be(original.Id);
            d.DisplayName.Should().Be(original.DisplayName);
            d.MatchLabels.Should().BeEquivalentTo(original.MatchLabels);
            d.Enabled.Should().Be(original.Enabled);
            d.ExecutionOrder.Should().Be(original.ExecutionOrder);
            d.Agents.Should().HaveCount(original.Agents.Count);

            for (var i = 0; i < original.Agents.Count; i++)
            {
                d.Agents[i].Name.Should().Be(original.Agents[i].Name,
                    because: $"ReviewAgent[{i}].Name must survive the roundtrip");
                d.Agents[i].Prompt.Should().Be(original.Agents[i].Prompt,
                    because: $"ReviewAgent[{i}].Prompt must survive the roundtrip");
            }
        });
    }

    // ── ProviderConfig (with Settings dictionary edge case) ───────────────

    [Property(MaxTest = 20)]
    public Property ProviderConfig_RoundTrip_PreservesAllFields()
    {
        var setupStepGen =
            from cmd in Gen.Elements("npm install", "pip install -r requirements.txt", "dotnet restore")
            from name in Gen.Elements("Install deps", "Setup env", "Restore packages")
            select new SetupStep { Command = cmd, Name = name };

        var gen =
            from id in Gen.Elements("pc-001", "pc-002", "pc-003")
            from displayName in Gen.Elements("GitHub Main", "KiroCli Agent", "Issue Provider")
            from kind in Gen.Elements(ProviderKind.Repository, ProviderKind.Agent, ProviderKind.Issue)
            from providerType in Gen.Elements("GitHub", "KiroCli", "GitLab")
            from role in Gen.Elements(RepositoryRole.Work, RepositoryRole.Brain)
            from hasBlacklist in Gen.Elements(true, false)
            from hasSecrets in Gen.Elements(true, false)
            from hasSetupSteps in Gen.Elements(true, false)
            from hasSteeringContent in Gen.Elements(true, false)
            from hasRequiredLabels in Gen.Elements(true, false)
            from stepCount in Gen.Choose(0, 2)
            from steps in Gen.ListOf(setupStepGen)
            select new ProviderConfig
            {
                Id = id,
                DisplayName = displayName,
                Kind = kind,
                ProviderType = providerType,
                RepositoryRole = role,
                BlacklistedPaths = hasBlacklist ? BlacklistPaths : null,
                Secrets = hasSecrets
                    ? new Dictionary<string, string> { ["TOKEN"] = "secret-value", ["API_KEY"] = "key-123" }
                    : null,
                Settings = new Dictionary<string, string>
                {
                    ["owner"] = "my-org",
                    ["repo"] = "my-repo",
                    ["baseUrl"] = "https://github.com"
                },
                SetupSteps = hasSetupSteps ? steps.Take(stepCount).ToList() : null,
                SteeringContent = hasSteeringContent ? "## Agent Guidelines\n\nFollow TDD." : null,
                RequiredLabels = hasRequiredLabels ? RequiredLabels : null
            };

        return Prop.ForAll(gen.ToArbitrary(), original =>
        {
            var d = RoundTrip(original);

            d.Id.Should().Be(original.Id);
            d.DisplayName.Should().Be(original.DisplayName);
            d.Kind.Should().Be(original.Kind);
            d.ProviderType.Should().Be(original.ProviderType);
            d.RepositoryRole.Should().Be(original.RepositoryRole);
            d.BlacklistedPaths.Should().BeEquivalentTo(original.BlacklistedPaths);
            d.RequiredLabels.Should().BeEquivalentTo(original.RequiredLabels);
            d.SteeringContent.Should().Be(original.SteeringContent);

            // Settings dictionary — historically a serialization edge case
            d.Settings.Should().BeEquivalentTo(original.Settings,
                because: "Settings dictionary must survive roundtrip (carries credentials for agent execution)");

            if (original.Secrets is null)
                d.Secrets.Should().BeNull();
            else
                d.Secrets.Should().BeEquivalentTo(original.Secrets,
                    because: "Secrets dictionary must survive roundtrip");

            if (original.SetupSteps is null)
            {
                d.SetupSteps.Should().BeNull();
            }
            else
            {
                d.SetupSteps.Should().HaveCount(original.SetupSteps.Count);
                for (var i = 0; i < original.SetupSteps.Count; i++)
                {
                    d.SetupSteps![i].Command.Should().Be(original.SetupSteps[i].Command);
                    d.SetupSteps[i].Name.Should().Be(original.SetupSteps[i].Name);
                }
            }
        });
    }

    // ── PipelineJobTemplate ───────────────────────────────────────────────

    [Property(MaxTest = 20)]
    public Property PipelineJobTemplate_RoundTrip_PreservesAllFields()
    {
        var gen =
            from id in Gen.Elements("tmpl-001", "tmpl-002", "tmpl-003")
            from name in Gen.Elements("DotNet Main", "Python Service", "Java API")
            from issueProviderId in Gen.Elements("ip-001", "ip-002")
            from repoProviderId in Gen.Elements("rp-001", "rp-002")
            from hasBrain in Gen.Elements(true, false)
            from brainReadOnly in Gen.Elements(true, false)
            from hasPipeline in Gen.Elements(true, false)
            from enabled in Gen.Elements(true, false)
            from implEnabled in Gen.Elements(true, false)
            from reviewEnabled in Gen.Elements(true, false)
            from decompEnabled in Gen.Elements(true, false)
            from housekeepingEnabled in Gen.Elements(true, false)
            from branchCleanupEnabled in Gen.Elements(true, false)
            from housekeepingLimit in Gen.Elements(null as int?, 2, 5)
            select new PipelineJobTemplate
            {
                Id = id,
                Name = name,
                IssueProviderId = issueProviderId,
                RepoProviderId = repoProviderId,
                BrainProviderId = hasBrain ? "bp-001" : null,
                BrainReadOnly = brainReadOnly,
                PipelineProviderId = hasPipeline ? "pp-001" : null,
                Enabled = enabled,
                ImplementationEnabled = implEnabled,
                ReviewEnabled = reviewEnabled,
                DecompositionEnabled = decompEnabled,
                HousekeepingEnabled = housekeepingEnabled,
                HousekeepingConcurrencyLimit = housekeepingLimit,
                HousekeepingBranchCleanupEnabled = branchCleanupEnabled
            };

        return Prop.ForAll(gen.ToArbitrary(), original =>
        {
            var d = RoundTrip(original);

            d.Id.Should().Be(original.Id);
            d.Name.Should().Be(original.Name);
            d.IssueProviderId.Should().Be(original.IssueProviderId);
            d.RepoProviderId.Should().Be(original.RepoProviderId);
            d.BrainProviderId.Should().Be(original.BrainProviderId);
            d.BrainReadOnly.Should().Be(original.BrainReadOnly);
            d.PipelineProviderId.Should().Be(original.PipelineProviderId);
            d.Enabled.Should().Be(original.Enabled);
            d.ImplementationEnabled.Should().Be(original.ImplementationEnabled);
            d.ReviewEnabled.Should().Be(original.ReviewEnabled);
            d.DecompositionEnabled.Should().Be(original.DecompositionEnabled);
            d.HousekeepingEnabled.Should().Be(original.HousekeepingEnabled);
            d.HousekeepingConcurrencyLimit.Should().Be(original.HousekeepingConcurrencyLimit);
            d.HousekeepingBranchCleanupEnabled.Should().Be(original.HousekeepingBranchCleanupEnabled);
        });
    }
}
