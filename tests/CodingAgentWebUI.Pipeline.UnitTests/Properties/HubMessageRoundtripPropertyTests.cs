using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using MessagePack;
using MessagePack.Resolvers;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based round-trip serialization tests for SignalR hub message types
/// not covered by the existing MessageSerializationPropertyTests.
/// Uses ContractlessStandardResolverAllowPrivate to match production SignalR config.
/// Verifies: any valid instance of each DTO survives serialize → deserialize without data loss.
/// </summary>
public class HubMessageRoundtripPropertyTests
{
    private static readonly MessagePackSerializerOptions MsgPackOptions =
        ContractlessStandardResolverAllowPrivate.Options;

    private static T RoundTrip<T>(T original)
    {
        var bytes = MessagePackSerializer.Serialize(original, MsgPackOptions);
        return MessagePackSerializer.Deserialize<T>(bytes, MsgPackOptions);
    }

    // ─── ActiveJobState ─────────────────────────────────────────────────────────

    /// <summary>
    /// ActiveJobState round-trip: all scalar properties survive MessagePack serialization.
    /// This DTO is sent as part of AgentRegistrationMessage.ActiveJob to re-track runs after restart.
    /// </summary>
    [Property(MaxTest = 50)]
    public bool ActiveJobState_RoundTrip_PreservesAllProperties(
        NonEmptyString runId,
        NonEmptyString issueId,
        NonEmptyString issueTitle,
        NonEmptyString issueProviderConfigId,
        NonEmptyString repoProviderConfigId,
        NonEmptyString agentProviderConfigId,
        NonEmptyString initiatedBy,
        bool hasOptionals)
    {
        var original = new ActiveJobState
        {
            RunId = runId.Get,
            IssueIdentifier = issueId.Get,
            IssueTitle = issueTitle.Get,
            IssueProviderConfigId = issueProviderConfigId.Get,
            RepoProviderConfigId = repoProviderConfigId.Get,
            AgentProviderConfigId = agentProviderConfigId.Get,
            InitiatedBy = initiatedBy.Get,
            BrainProviderConfigId = hasOptionals ? "brain-1" : null,
            PipelineProviderConfigId = hasOptionals ? "pipeline-1" : null,
            ResolvedProfileId = hasOptionals ? "profile-1" : null,
            ProjectId = hasOptionals ? "proj-1" : null,
            ProjectName = hasOptionals ? "Test Project" : null,
            CurrentStep = PipelineStep.GeneratingCode,
            StartedAt = DateTimeOffset.UtcNow,
            RunType = PipelineRunType.Implementation,
            RepositoryName = hasOptionals ? "test-repo" : null,
            ModelName = hasOptionals ? "claude-sonnet" : null
        };

        var deserialized = RoundTrip(original);

        return deserialized.RunId == original.RunId
            && deserialized.IssueIdentifier == original.IssueIdentifier
            && deserialized.IssueTitle == original.IssueTitle
            && deserialized.IssueProviderConfigId == original.IssueProviderConfigId
            && deserialized.RepoProviderConfigId == original.RepoProviderConfigId
            && deserialized.AgentProviderConfigId == original.AgentProviderConfigId
            && deserialized.InitiatedBy == original.InitiatedBy
            && deserialized.BrainProviderConfigId == original.BrainProviderConfigId
            && deserialized.PipelineProviderConfigId == original.PipelineProviderConfigId
            && deserialized.ResolvedProfileId == original.ResolvedProfileId
            && deserialized.ProjectId == original.ProjectId
            && deserialized.ProjectName == original.ProjectName
            && deserialized.CurrentStep == original.CurrentStep
            && deserialized.RunType == original.RunType
            && deserialized.RepositoryName == original.RepositoryName
            && deserialized.ModelName == original.ModelName;
    }

    // ─── ChatPromptMessage ──────────────────────────────────────────────────────

    /// <summary>
    /// ChatPromptMessage round-trip: interactive chat assignment survives serialization.
    /// Verifies SessionId, Prompt, UseResume, McpConfigPath all preserved.
    /// </summary>
    [Property(MaxTest = 50)]
    public bool ChatPromptMessage_RoundTrip_PreservesAllProperties(
        NonEmptyString sessionId,
        NonEmptyString prompt,
        bool useResume)
    {
        var original = new ChatPromptMessage
        {
            SessionId = sessionId.Get,
            Prompt = prompt.Get,
            UseResume = useResume,
            McpServers = new List<McpServerConfig>
            {
                new() { Name = "test-server", Type = "stdio", Command = "npx", Args = new[] { "-y", "mcp" } }
            },
            McpConfigPath = "/home/user/.kiro/settings/mcp.json"
        };

        var deserialized = RoundTrip(original);

        return deserialized.SessionId == original.SessionId
            && deserialized.Prompt == original.Prompt
            && deserialized.UseResume == original.UseResume
            && deserialized.McpConfigPath == original.McpConfigPath
            && deserialized.McpServers.Count == 1
            && deserialized.McpServers[0].Name == "test-server";
    }

    // ─── ChatResponseMessage ────────────────────────────────────────────────────

    /// <summary>
    /// ChatResponseMessage round-trip: streamed chat lines survive serialization.
    /// </summary>
    [Property(MaxTest = 50)]
    public bool ChatResponseMessage_RoundTrip_PreservesLinesAndSessionId(
        NonEmptyString sessionId,
        NonEmptyString[] lines)
    {
        var linesList = lines.Select(l => l.Get).ToList();
        var original = new ChatResponseMessage
        {
            SessionId = sessionId.Get,
            Lines = linesList
        };

        var deserialized = RoundTrip(original);

        return deserialized.SessionId == original.SessionId
            && deserialized.Lines.Count == linesList.Count
            && deserialized.Lines.SequenceEqual(linesList);
    }

    // ─── ChatCompletedMessage ───────────────────────────────────────────────────

    /// <summary>
    /// ChatCompletedMessage round-trip: exit code and optional error survive.
    /// </summary>
    [Property(MaxTest = 50)]
    public bool ChatCompletedMessage_RoundTrip_PreservesAllProperties(
        NonEmptyString sessionId,
        int exitCode,
        bool hasError)
    {
        var original = new ChatCompletedMessage
        {
            SessionId = sessionId.Get,
            ExitCode = exitCode,
            Error = hasError ? "Something went wrong" : null
        };

        var deserialized = RoundTrip(original);

        return deserialized.SessionId == original.SessionId
            && deserialized.ExitCode == original.ExitCode
            && deserialized.Error == original.Error;
    }

    // ─── FetchModelsRequest / FetchModelsResponse ───────────────────────────────

    /// <summary>
    /// FetchModelsRequest round-trip: trivial message with just RequestId.
    /// </summary>
    [Property(MaxTest = 50)]
    public bool FetchModelsRequest_RoundTrip(NonEmptyString requestId)
    {
        var original = new FetchModelsRequest { RequestId = requestId.Get };

        var deserialized = RoundTrip(original);

        return deserialized.RequestId == original.RequestId;
    }

    /// <summary>
    /// FetchModelsResponse round-trip: model list with nested AgentModelInfo survives.
    /// </summary>
    [Property(MaxTest = 50)]
    public bool FetchModelsResponse_RoundTrip_PreservesModels(
        NonEmptyString requestId,
        bool hasError)
    {
        var original = new FetchModelsResponse
        {
            RequestId = requestId.Get,
            Models = new List<AgentModelInfo>
            {
                new() { ModelId = "claude-sonnet-4", Description = "Balanced model", RateMultiplier = 1.0 },
                new() { ModelId = "claude-opus-4", Description = "Reasoning model", RateMultiplier = 5.0 }
            },
            Error = hasError ? "Provider unavailable" : null
        };

        var deserialized = RoundTrip(original);

        return deserialized.RequestId == original.RequestId
            && deserialized.Error == original.Error
            && deserialized.Models.Count == 2
            && deserialized.Models[0].ModelId == "claude-sonnet-4"
            && deserialized.Models[0].RateMultiplier == 1.0
            && deserialized.Models[1].ModelId == "claude-opus-4"
            && deserialized.Models[1].RateMultiplier == 5.0;
    }

    // ─── ConsolidationJobMessage ────────────────────────────────────────────────

    /// <summary>
    /// ConsolidationJobMessage round-trip: all fields including optional WorkspacePath,
    /// LastSuccessfulRunUtc, FeedbackDataJson, and TraceContext.
    /// </summary>
    [Property(MaxTest = 50)]
    public bool ConsolidationJobMessage_RoundTrip_PreservesAllProperties(
        NonEmptyString jobId,
        bool hasOptionals)
    {
        var original = new ConsolidationJobMessage
        {
            JobId = jobId.Get,
            Type = ConsolidationRunType.BrainConsolidation,
            TemplateId = hasOptionals ? "tmpl-1" : null,
            TemplateName = hasOptionals ? "Backend" : null,
            ProviderConfigs = new List<ProviderConfig>
            {
                new()
                {
                    Id = "pc-1",
                    Kind = ProviderKind.Repository,
                    ProviderType = "GitHub",
                    DisplayName = "Test",
                    Settings = new Dictionary<string, string> { ["owner"] = "org" }
                }
            },
            PipelineConfiguration = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = "/tmp/test"
            },
            LastSuccessfulRunUtc = hasOptionals ? new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc) : null,
            FeedbackDataJson = hasOptionals ? "{\"entries\":[]}" : null,
            WorkspacePath = hasOptionals ? "/workspaces/consol-123" : null,
            TraceContext = hasOptionals
                ? new Dictionary<string, string> { ["traceparent"] = "00-abc-def-01" }
                : null
        };

        var deserialized = RoundTrip(original);

        return deserialized.JobId == original.JobId
            && deserialized.Type == original.Type
            && deserialized.TemplateId == original.TemplateId
            && deserialized.TemplateName == original.TemplateName
            && deserialized.ProviderConfigs.Count == 1
            && deserialized.ProviderConfigs[0].Id == "pc-1"
            && deserialized.LastSuccessfulRunUtc == original.LastSuccessfulRunUtc
            && deserialized.FeedbackDataJson == original.FeedbackDataJson
            && deserialized.WorkspacePath == original.WorkspacePath
            && (deserialized.TraceContext == null) == (original.TraceContext == null);
    }

    // ─── AgentRegistrationMessage with ActiveJobState ───────────────────────────

    /// <summary>
    /// AgentRegistrationMessage with nested ActiveJobState round-trip.
    /// Verifies the new ActiveJob field (added for restart re-tracking) survives serialization.
    /// </summary>
    [Property(MaxTest = 50)]
    public bool AgentRegistrationMessage_WithActiveJob_RoundTrip(
        NonEmptyString agentId,
        NonEmptyString hostname,
        bool hasActiveJob)
    {
        var original = new AgentRegistrationMessage
        {
            AgentId = agentId.Get,
            Hostname = hostname.Get,
            Labels = new List<string> { "dotnet", "linux" },
            ActiveJob = hasActiveJob
                ? new ActiveJobState
                {
                    RunId = "run-1",
                    IssueIdentifier = "org/repo#42",
                    IssueTitle = "Test issue",
                    IssueProviderConfigId = "ip-1",
                    RepoProviderConfigId = "rp-1",
                    AgentProviderConfigId = "ap-1",
                    InitiatedBy = "loop",
                    CurrentStep = PipelineStep.GeneratingCode,
                    StartedAt = DateTimeOffset.UtcNow
                }
                : null
        };

        var deserialized = RoundTrip(original);

        var basicMatch = deserialized.AgentId == original.AgentId
            && deserialized.Hostname == original.Hostname
            && deserialized.Labels.Count == 2;

        if (!hasActiveJob)
            return basicMatch && deserialized.ActiveJob == null;

        return basicMatch
            && deserialized.ActiveJob != null
            && deserialized.ActiveJob.RunId == "run-1"
            && deserialized.ActiveJob.IssueIdentifier == "org/repo#42"
            && deserialized.ActiveJob.CurrentStep == PipelineStep.GeneratingCode;
    }
}
