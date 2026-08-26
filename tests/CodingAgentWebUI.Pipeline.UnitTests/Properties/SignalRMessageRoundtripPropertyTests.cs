using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using MessagePack;
using MessagePack.Resolvers;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// MessagePack serialization round-trip property tests for the SignalR dispatch DTOs.
///
/// All ~35 <see cref="MessagePackObjectAttribute"/>-annotated DTOs in SignalRMessages.cs and
/// related files had zero roundtrip tests. A missing <c>[Key(N)]</c> attribute on any new
/// field silently drops that field over the wire — the agent receives a corrupt job context
/// with no compile-time or unit-test signal.
///
/// These tests use the same ContractlessStandardResolverAllowPrivate options as every other
/// MessagePack property test in the codebase.
/// </summary>
public class SignalRMessageRoundtripPropertyTests
{
    private static readonly MessagePackSerializerOptions Options =
        ContractlessStandardResolverAllowPrivate.Options;

    private static T RoundTrip<T>(T original)
    {
        var bytes = MessagePackSerializer.Serialize(original, Options);
        return MessagePackSerializer.Deserialize<T>(bytes, Options);
    }

    // ── Generators ─────────────────────────────────────────────────────────────

    private static Gen<IssueDetail> IssueDetailGen =>
        from title in Gen.Elements("Fix login bug", "Add pagination", "Extract service")
        from id in Gen.Elements("42", "123", "999")
        from desc in Gen.Elements("User cannot log in", "API lacks pagination", "Service is too large")
        select new IssueDetail
        {
            Title = title,
            Identifier = id,
            Description = desc,
            Labels = ["dotnet", "bug"]
        };

    private static Gen<ParsedIssue> ParsedIssueGen =>
        from req in Gen.Elements("Must fix the login flow", "API must support cursor pagination")
        from count in Gen.Choose(0, 3)
        from criteria in Gen.ListOf(Gen.Elements("Login works", "Pages load", "Service isolated"))
        select new ParsedIssue
        {
            RequirementsSection = req,
            AcceptanceCriteria = criteria.Take(count).ToList()
        };

    private static Gen<PipelineConfiguration> PipelineConfigGen =>
        from retries in Gen.Choose(1, 10)
        from workspace in Gen.Elements("/workspace/runs", "/tmp/agent")
        select new PipelineConfiguration
        {
            MaxRetries = retries,
            WorkspaceBaseDirectory = workspace
        };

    // ── JobAssignmentMessage ───────────────────────────────────────────────────

    /// <summary>
    /// JobAssignmentMessage is the primary SignalR dispatch payload. All fields
    /// that survive the wire must round-trip cleanly.
    /// </summary>
    [Property(MaxTest = 20)]
    public Property JobAssignmentMessage_RoundTrip_PreservesAllFields()
    {
        var gen =
            from jobId in Gen.Elements("job-1", "job-abc", "job-xyz")
            from issueId in Gen.Elements("42", "100", "9999")
            from detail in IssueDetailGen
            from parsed in ParsedIssueGen
            from pipelineCfg in PipelineConfigGen
            from repoProvId in Gen.Elements("repo-1", "repo-github")
            from agentProvId in Gen.Elements("agent-kiro", "agent-opencode")
            from initiatedBy in Gen.Elements("loop", "manual", "consolidation")
            from runType in Gen.Elements(PipelineRunType.Implementation, PipelineRunType.Review)
            select new JobAssignmentMessage
            {
                JobId = jobId,
                IssueIdentifier = issueId,
                IssueDetail = detail,
                ParsedIssue = parsed,
                IssueComments = [],
                RepoProviderConfigId = repoProvId,
                AgentProviderConfigId = agentProvId,
                ProviderConfigs = [],
                PipelineConfiguration = pipelineCfg,
                InitiatedBy = initiatedBy,
                QualityGateConfigs = [],
                RunType = runType
            };

        return Prop.ForAll(gen.ToArbitrary(), (JobAssignmentMessage original) =>
        {
            var deserialized = RoundTrip(original);
            deserialized.IssueIdentifier.Should().Be(original.IssueIdentifier);
            deserialized.IssueDetail.Should().NotBeNull();
            deserialized.IssueDetail.Title.Should().Be(original.IssueDetail.Title);
            deserialized.IssueDetail.Identifier.Should().Be(original.IssueDetail.Identifier);
            deserialized.ParsedIssue.Should().NotBeNull();
            deserialized.ParsedIssue.RequirementsSection.Should().Be(original.ParsedIssue.RequirementsSection);
            deserialized.RepoProviderConfigId.Should().Be(original.RepoProviderConfigId);
            deserialized.AgentProviderConfigId.Should().Be(original.AgentProviderConfigId);
            deserialized.PipelineConfiguration.Should().NotBeNull();
            deserialized.PipelineConfiguration.MaxRetries.Should().Be(original.PipelineConfiguration.MaxRetries);
            deserialized.PipelineConfiguration.WorkspaceBaseDirectory.Should().Be(original.PipelineConfiguration.WorkspaceBaseDirectory);
            deserialized.InitiatedBy.Should().Be(original.InitiatedBy);
            deserialized.RunType.Should().Be(original.RunType);
        });
    }

    // ── AgentRegistrationMessage ───────────────────────────────────────────────

    [Property(MaxTest = 20)]
    public Property AgentRegistrationMessage_RoundTrip_PreservesFields()
    {
        var gen =
            from agentId in Gen.Elements("agent-1", "agent-abc")
            from hostname in Gen.Elements("pod-1", "worker-node-7")
            from labelCount in Gen.Choose(1, 4)
            from labels in Gen.ListOf(Gen.Elements("dotnet", "java", "python", "typescript"))
            select new AgentRegistrationMessage
            {
                AgentId = agentId,
                Hostname = hostname,
                Labels = labels.Take(labelCount).ToList()
            };

        return Prop.ForAll(gen.ToArbitrary(), (AgentRegistrationMessage original) =>
        {
            var deserialized = RoundTrip(original);

            deserialized.AgentId.Should().Be(original.AgentId);
            deserialized.Hostname.Should().Be(original.Hostname);
            deserialized.Labels.Should().BeEquivalentTo(original.Labels);
        });
    }

    // ── HeartbeatMessage ───────────────────────────────────────────────────────

    [Property(MaxTest = 20)]
    public Property HeartbeatMessage_RoundTrip_PreservesFields()
    {
        var gen =
            from agentId in Gen.Elements("agent-1", "agent-2")
            from ts in Gen.Choose(0, 1_000_000).Select(t =>
                new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(t))
            from step in Gen.Elements<PipelineStep?>(null, PipelineStep.RunningQualityGates, PipelineStep.Completed)
            from mem in Gen.Choose(0, 8192).Select(m => (long)m)
            select new HeartbeatMessage
            {
                AgentId = agentId,
                Timestamp = ts,
                CurrentStep = step,
                MemoryUsageMb = mem
            };

        return Prop.ForAll(gen.ToArbitrary(), (HeartbeatMessage original) =>
        {
            var deserialized = RoundTrip(original);

            deserialized.AgentId.Should().Be(original.AgentId);
            deserialized.Timestamp.Should().Be(original.Timestamp);
            deserialized.CurrentStep.Should().Be(original.CurrentStep);
            deserialized.MemoryUsageMb.Should().Be(original.MemoryUsageMb);
        });
    }

    // ── ActiveJobState ─────────────────────────────────────────────────────────

    [Property(MaxTest = 20)]
    public Property ActiveJobState_RoundTrip_PreservesFields()
    {
        var gen =
            from runId in Gen.Elements("run-1", "run-abc")
            from issueId in Gen.Elements("42", "999")
            from issueTitle in Gen.Elements("Fix bug", "Add feature")
            from issueProvId in Gen.Elements("ip-1", "ip-github")
            from repoProvId in Gen.Elements("rp-1", "rp-github")
            from agentProvId in Gen.Elements("ap-kiro", "ap-opencode")
            from initiatedBy in Gen.Elements("loop", "manual")
            from step in Gen.Elements(PipelineStep.RunningQualityGates, PipelineStep.Completed, PipelineStep.Failed)
            from ts in Gen.Choose(0, 100_000).Select(t =>
                new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(t))
            from runType in Gen.Elements(PipelineRunType.Implementation, PipelineRunType.Review)
            select new ActiveJobState
            {
                RunId = runId,
                IssueIdentifier = issueId,
                IssueTitle = issueTitle,
                IssueProviderConfigId = issueProvId,
                RepoProviderConfigId = repoProvId,
                AgentProviderConfigId = agentProvId,
                InitiatedBy = initiatedBy,
                CurrentStep = step,
                StartedAt = ts,
                RunType = runType
            };

        return Prop.ForAll(gen.ToArbitrary(), (ActiveJobState original) =>
        {
            var deserialized = RoundTrip(original);
            deserialized.IssueIdentifier.Should().Be(original.IssueIdentifier);
            deserialized.IssueTitle.Should().Be(original.IssueTitle);
            deserialized.CurrentStep.Should().Be(original.CurrentStep);
            deserialized.StartedAt.Should().Be(original.StartedAt);
            deserialized.RunType.Should().Be(original.RunType);
        });
    }

    // ── JobAssignmentMessage with optional fields ──────────────────────────────

    [Fact]
    public void JobAssignmentMessage_WithLinkedPullRequest_SurvivesRoundTrip()
    {
        var original = BuildMinimalJobAssignment() with
        {
            LinkedPullRequest = new LinkedPullRequest
            {
                BranchName = "feature/fix-login",
                IsDraft = false,
                Number = 99,
                Url = "https://github.com/org/repo/pull/99"
            }
        };

        var deserialized = RoundTrip(original);

        deserialized.LinkedPullRequest.Should().NotBeNull();
        deserialized.LinkedPullRequest!.BranchName.Should().Be("feature/fix-login");
        deserialized.LinkedPullRequest.Number.Should().Be(99);
        deserialized.LinkedPullRequest.IsDraft.Should().BeFalse();
    }

    [Fact]
    public void JobAssignmentMessage_NullOptionalFields_SurvivesRoundTrip()
    {
        var original = BuildMinimalJobAssignment();

        var deserialized = RoundTrip(original);

        deserialized.ExistingAnalysis.Should().BeNull();
        deserialized.LinkedPullRequest.Should().BeNull();
        deserialized.BrainProviderConfigId.Should().BeNull();
        deserialized.PipelineProviderConfigId.Should().BeNull();
        deserialized.LinkedIssueContexts.Should().BeNull();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static JobAssignmentMessage BuildMinimalJobAssignment() =>
        new()
        {
            JobId = "job-minimal",
            IssueIdentifier = "1",
            IssueDetail = new IssueDetail
            {
                Title = "Test", Identifier = "1",
                Description = "desc", Labels = []
            },
            ParsedIssue = new ParsedIssue
            {
                AcceptanceCriteria = [], RequirementsSection = ""
            },
            IssueComments = [],
            RepoProviderConfigId = "rp-1",
            AgentProviderConfigId = "ap-1",
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration(),
            InitiatedBy = "loop",
            QualityGateConfigs = []
        };
}
