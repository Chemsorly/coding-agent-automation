using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using MessagePack;
using MessagePack.Resolvers;
using CodingAgent.Pipeline.Models;
using FsCheck.Fluent;

namespace CodingAgent.Pipeline.UnitTests.Properties;

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
    [Property(MaxTest = 20)]
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
    [Property(MaxTest = 20)]
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
    [Property(MaxTest = 20)]
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
    [Property(MaxTest = 20)]
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
    [Property(MaxTest = 20)]
    public bool FetchModelsRequest_RoundTrip(NonEmptyString requestId)
    {
        var original = new FetchModelsRequest { RequestId = requestId.Get };

        var deserialized = RoundTrip(original);

        return deserialized.RequestId == original.RequestId;
    }

    /// <summary>
    /// FetchModelsResponse round-trip: model list with nested AgentModelInfo survives.
    /// </summary>
    [Property(MaxTest = 20)]
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
    /// ConsolidationJobMessage round-trip: all fields including the optional
    /// LastSuccessfulRunUtc, FeedbackDataJson, and TraceContext.
    /// </summary>
    [Property(MaxTest = 20)]
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
            && (deserialized.TraceContext == null) == (original.TraceContext == null);
    }

    [Property(MaxTest = 20)]
    public Property ConsolidationJobMessage_RoundTrip_PreservesAllFields()
    {
        // Generator for PipelineConfiguration — include fields most likely to change across schema evolutions
        var pipelineConfigGen =
            from maxRetries in Gen.Choose(1, 10)
            from agentTimeoutMin in Gen.Choose(5, 120)
            from workspace in Gen.Elements("/tmp/ws-a", "/tmp/ws-b", null as string)
            select new PipelineConfiguration
            {
                MaxRetries = maxRetries,
                AgentTimeout = TimeSpan.FromMinutes(agentTimeoutMin),
                WorkspaceBaseDirectory = workspace
            };

        // Generator for ProviderConfig list (1–3 entries)
        var providerGen =
            from kind in Gen.Elements(ProviderKind.Issue, ProviderKind.Repository, ProviderKind.Agent)
            from id in Gen.Elements("p-1", "p-2", "p-3")
            from providerType in Gen.Elements("GitHub", "GitLab", "KiroCli")
            from displayName in Gen.Elements("Test Provider", "CI Provider", "Agent A")
            select new ProviderConfig
            {
                Id = id,
                Kind = kind,
                ProviderType = providerType,
                DisplayName = displayName,
                Settings = new Dictionary<string, string>()
            };

        var gen =
            from jobId in Gen.Elements("job-a", "job-b", "consolidation-xyz")
            from runType in Gen.Elements(ConsolidationRunType.BrainConsolidation, ConsolidationRunType.HarnessSuggestions)
            from hasTemplate in Gen.Elements(true, false)
            from templateId in Gen.Elements("tmpl-1", "tmpl-2")
            from templateName in Gen.Elements("Brain sync", "Harness")
            from providerCount in Gen.Choose(1, 3)
            from providers in Gen.ListOf(providerGen)
            from pipelineConfig in pipelineConfigGen
            from hasLastRun in Gen.Elements(true, false)
            from hasFeedback in Gen.Elements(true, false)
            from feedbackJson in Gen.Elements("[{\"type\":\"positive\"}]", "[]")
            from hasTrace in Gen.Elements(true, false)
            from autoDispatch in Gen.Elements(true, false)
            select new ConsolidationJobMessage
            {
                JobId = jobId,
                Type = runType,
                TemplateId = hasTemplate ? templateId : null,
                TemplateName = hasTemplate ? templateName : null,
                ProviderConfigs = providers.Take(providerCount).ToList(),
                PipelineConfiguration = pipelineConfig,
                LastSuccessfulRunUtc = hasLastRun ? new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc) : null,
                FeedbackDataJson = hasFeedback ? feedbackJson : null,
                TraceContext = hasTrace
                    ? new Dictionary<string, string> { ["traceparent"] = "00-abc123-def456-01" }
                    : null,
                AutoDispatch = autoDispatch
            };

        return Prop.ForAll(gen.ToArbitrary(), original =>
        {
            var deserialized = RoundTrip(original);

            deserialized.JobId.Should().Be(original.JobId);
            deserialized.Type.Should().Be(original.Type);
            deserialized.TemplateId.Should().Be(original.TemplateId);
            deserialized.TemplateName.Should().Be(original.TemplateName);

            // ProviderConfigs list preserved
            deserialized.ProviderConfigs.Should().HaveCount(original.ProviderConfigs.Count,
                because: "Key(4) ProviderConfigs list must survive MessagePack roundtrip");
            for (var i = 0; i < original.ProviderConfigs.Count; i++)
            {
                deserialized.ProviderConfigs[i].Id.Should().Be(original.ProviderConfigs[i].Id);
                deserialized.ProviderConfigs[i].Kind.Should().Be(original.ProviderConfigs[i].Kind);
                deserialized.ProviderConfigs[i].ProviderType.Should().Be(original.ProviderConfigs[i].ProviderType);
                deserialized.ProviderConfigs[i].DisplayName.Should().Be(original.ProviderConfigs[i].DisplayName);
            }

            // PipelineConfiguration nested object preserved
            deserialized.PipelineConfiguration.MaxRetries.Should().Be(original.PipelineConfiguration.MaxRetries);
            deserialized.PipelineConfiguration.AgentTimeout.Should().Be(original.PipelineConfiguration.AgentTimeout);
            deserialized.PipelineConfiguration.WorkspaceBaseDirectory.Should()
                .Be(original.PipelineConfiguration.WorkspaceBaseDirectory);

            // Optional fields preserved (null or value)
            deserialized.LastSuccessfulRunUtc.Should().Be(original.LastSuccessfulRunUtc);
            deserialized.FeedbackDataJson.Should().Be(original.FeedbackDataJson);
            deserialized.AutoDispatch.Should().Be(original.AutoDispatch,
                because: "Key(10) AutoDispatch bool must survive MessagePack roundtrip");

            // TraceContext dictionary preserved (null or populated)
            if (original.TraceContext is null)
                deserialized.TraceContext.Should().BeNull();
            else
                deserialized.TraceContext.Should().BeEquivalentTo(original.TraceContext,
                    because: "Key(9) TraceContext Dictionary must survive MessagePack roundtrip");
        });
    }

    [Fact]
    public void ConsolidationJobMessage_MinimalRequired_SurvivesRoundTrip()
    {
        // Verifies that the required fields alone round-trip correctly (no optional fields).
        var original = new ConsolidationJobMessage
        {
            JobId = "minimal-job",
            Type = ConsolidationRunType.BrainConsolidation,
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration()
        };

        var deserialized = RoundTrip(original);

        deserialized.JobId.Should().Be("minimal-job");
        deserialized.Type.Should().Be(ConsolidationRunType.BrainConsolidation);
        deserialized.ProviderConfigs.Should().BeEmpty();
        deserialized.TemplateId.Should().BeNull();
        deserialized.FeedbackDataJson.Should().BeNull();
        deserialized.TraceContext.Should().BeNull();
        deserialized.AutoDispatch.Should().BeFalse();
    }

    // ─── AgentRegistrationMessage with ActiveJobState ───────────────────────────

    /// <summary>
    /// AgentRegistrationMessage with nested ActiveJobState round-trip.
    /// Verifies the new ActiveJob field (added for restart re-tracking) survives serialization.
    /// </summary>
    [Property(MaxTest = 20)]
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

    // ── DecompositionProjectContext ──────────────────────────────────────

    [Property(MaxTest = 20)]
    public Property DecompositionProjectContext_RoundTrip_PreservesFields()
    {
        var repoTargetGen =
            from available in Gen.Elements(true, false)
            from decompEnabled in Gen.Elements(true, false)
            from desc in Gen.Elements("Web API service", "Frontend SPA", "Shared library")
            from hasIssueProv in Gen.Elements(true, false)
            from hasLocalPath in Gen.Elements(true, false)
            from hasRepoProv in Gen.Elements(true, false)
            from templateName in Gen.Elements("api-svc", "web-ui", "core-lib")
            from labelCount in Gen.Choose(0, 3)
            from labels in Gen.ListOf(Gen.Elements("csharp", "typescript", "python", "go"))
            select new RepositoryTarget
            {
                Available = available,
                DecompositionEnabled = decompEnabled,
                Description = desc,
                IssueProviderId = hasIssueProv ? "ip-001" : null,
                Labels = labels.Take(3).ToList(),
                LocalPath = hasLocalPath ? "/repos/my-repo" : null,
                RepoProviderId = hasRepoProv ? "rp-001" : null,
                TemplateName = templateName
            };

        var gen =
            from projectName in Gen.Elements("MyProject", "Backend", "Platform")
            from repoCount in Gen.Choose(1, 4)
            from repos in Gen.ListOf(repoTargetGen)
            select new DecompositionProjectContext
            {
                ProjectName = projectName,
                Repositories = repos.Take(repoCount).ToList()
            };

        return Prop.ForAll(gen.ToArbitrary(), original =>
        {
            var deserialized = RoundTrip(original);

            deserialized.ProjectName.Should().Be(original.ProjectName);
            deserialized.Repositories.Should().HaveCount(original.Repositories.Count);

            for (var i = 0; i < original.Repositories.Count; i++)
            {
                var orig = original.Repositories[i];
                var deser = deserialized.Repositories[i];
                deser.Available.Should().Be(orig.Available);
                deser.DecompositionEnabled.Should().Be(orig.DecompositionEnabled);
                deser.Description.Should().Be(orig.Description);
                deser.IssueProviderId.Should().Be(orig.IssueProviderId);
                deser.Labels.Should().BeEquivalentTo(orig.Labels);
                deser.LocalPath.Should().Be(orig.LocalPath);
                deser.RepoProviderId.Should().Be(orig.RepoProviderId);
                deser.TemplateName.Should().Be(orig.TemplateName);
            }
        });
    }

    // ── ConsolidationJobResult ───────────────────────────────────────────

    [Property(MaxTest = 20)]
    public Property ConsolidationJobResult_RoundTrip_PreservesFields()
    {
        var tokenUsageGen =
            from input in Gen.Choose(0, 100_000).Select(i => (long)i)
            from output in Gen.Choose(0, 50_000).Select(i => (long)i)
            from reasoning in Gen.Choose(0, 20_000).Select(i => (long)i)
            from cacheRead in Gen.Choose(0, 80_000).Select(i => (long)i)
            from cacheWrite in Gen.Choose(0, 30_000).Select(i => (long)i)
            select new TokenUsage
            {
                InputTokens = input,
                OutputTokens = output,
                ReasoningTokens = reasoning,
                CacheReadTokens = cacheRead,
                CacheWriteTokens = cacheWrite
            };

        var createdIssueGen =
            from id in Gen.Elements("123", "456", "789")
            from title in Gen.Elements("Refactor module", "Extract service", "Fix coupling")
            select new CreatedIssueInfo
            {
                Identifier = id,
                Title = title,
                Url = $"https://github.com/org/repo/issues/{id}"
            };

        var gen =
            from jobId in Gen.Elements("job-1", "job-2", "job-abc")
            from success in Gen.Elements(true, false)
            from hasSummary in Gen.Elements(true, false)
            from hasError in Gen.Elements(true, false)
            from issueCount in Gen.Choose(0, 3)
            from issues in Gen.ListOf(createdIssueGen)
            from hasTokens in Gen.Elements(true, false)
            from tokens in tokenUsageGen
            select new ConsolidationJobResult
            {
                JobId = jobId,
                Success = success,
                Summary = hasSummary ? "Completed 3 refactoring proposals" : null,
                ErrorMessage = hasError ? "Agent timed out" : null,
                CreatedIssues = issueCount > 0 ? issues.Take(issueCount).ToList() : null,
                HarnessSuggestions = null, // tested separately below
                ReviewTokenUsage = hasTokens ? tokens : null,
                RefinementTokenUsage = hasTokens ? tokens : null,
                DiffSummaryTokenUsage = null
            };

        return Prop.ForAll(gen.ToArbitrary(), original =>
        {
            var deserialized = RoundTrip(original);

            deserialized.JobId.Should().Be(original.JobId);
            deserialized.Success.Should().Be(original.Success);
            deserialized.Summary.Should().Be(original.Summary);
            deserialized.ErrorMessage.Should().Be(original.ErrorMessage);

            if (original.CreatedIssues is null)
            {
                deserialized.CreatedIssues.Should().BeNull();
            }
            else
            {
                deserialized.CreatedIssues.Should().HaveCount(original.CreatedIssues.Count);
                for (var i = 0; i < original.CreatedIssues.Count; i++)
                {
                    deserialized.CreatedIssues![i].Identifier.Should().Be(original.CreatedIssues[i].Identifier);
                    deserialized.CreatedIssues[i].Title.Should().Be(original.CreatedIssues[i].Title);
                    deserialized.CreatedIssues[i].Url.Should().Be(original.CreatedIssues[i].Url);
                }
            }

            if (original.ReviewTokenUsage is null)
                deserialized.ReviewTokenUsage.Should().BeNull();
            else
            {
                deserialized.ReviewTokenUsage!.InputTokens.Should().Be(original.ReviewTokenUsage.InputTokens);
                deserialized.ReviewTokenUsage.OutputTokens.Should().Be(original.ReviewTokenUsage.OutputTokens);
                deserialized.ReviewTokenUsage.ReasoningTokens.Should().Be(original.ReviewTokenUsage.ReasoningTokens);
                deserialized.ReviewTokenUsage.CacheReadTokens.Should().Be(original.ReviewTokenUsage.CacheReadTokens);
                deserialized.ReviewTokenUsage.CacheWriteTokens.Should().Be(original.ReviewTokenUsage.CacheWriteTokens);
            }
        });
    }

    // ── HarnessSuggestions ───────────────────────────────────────────────

    [Property(MaxTest = 20)]
    public Property HarnessSuggestions_RoundTrip_PreservesFields()
    {
        var suggestionGen =
            from freq in Gen.Choose(1, 50)
            from text in Gen.Elements(
                "Include tsconfig.json in initial context",
                "Add retry for MCP tool calls",
                "Provide DB schema upfront")
            from rationale in Gen.Elements(
                "Reported by 12 runs in last week",
                "3 timeouts in last 24h",
                "Agent asked for schema in 8/10 runs")
            select new HarnessSuggestion
            {
                Frequency = freq,
                Text = text,
                Rationale = rationale
            };

        var gen =
            from runCount in Gen.Choose(5, 200)
            from successRate in Gen.Choose(0, 100).Select(i => (decimal)i / 100)
            from suggCount in Gen.Choose(1, 5)
            from suggestions in Gen.ListOf(suggestionGen)
            select new HarnessSuggestions
            {
                BasedOnRunCount = runCount,
                GeneratedAtUtc = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
                SuccessRate = successRate,
                Suggestions = suggestions.Take(suggCount).ToList()
            };

        return Prop.ForAll(gen.ToArbitrary(), original =>
        {
            var deserialized = RoundTrip(original);

            deserialized.BasedOnRunCount.Should().Be(original.BasedOnRunCount);
            deserialized.GeneratedAtUtc.Should().Be(original.GeneratedAtUtc);
            deserialized.SuccessRate.Should().Be(original.SuccessRate);
            deserialized.Suggestions.Should().HaveCount(original.Suggestions.Count);

            for (var i = 0; i < original.Suggestions.Count; i++)
            {
                deserialized.Suggestions[i].Frequency.Should().Be(original.Suggestions[i].Frequency);
                deserialized.Suggestions[i].Text.Should().Be(original.Suggestions[i].Text);
                deserialized.Suggestions[i].Rationale.Should().Be(original.Suggestions[i].Rationale);
            }
        });
    }

    // ── TokenUsage (standalone) ──────────────────────────────────────────

    [Property(MaxTest = 20)]
    public Property TokenUsage_RoundTrip_PreservesAllFields()
    {
        var gen =
            from input in Gen.Choose(0, int.MaxValue).Select(i => (long)i)
            from output in Gen.Choose(0, int.MaxValue).Select(i => (long)i)
            from reasoning in Gen.Choose(0, int.MaxValue).Select(i => (long)i)
            from cacheRead in Gen.Choose(0, int.MaxValue).Select(i => (long)i)
            from cacheWrite in Gen.Choose(0, int.MaxValue).Select(i => (long)i)
            select new TokenUsage
            {
                InputTokens = input,
                OutputTokens = output,
                ReasoningTokens = reasoning,
                CacheReadTokens = cacheRead,
                CacheWriteTokens = cacheWrite
            };

        return Prop.ForAll(gen.ToArbitrary(), original =>
        {
            var deserialized = RoundTrip(original);

            deserialized.InputTokens.Should().Be(original.InputTokens);
            deserialized.OutputTokens.Should().Be(original.OutputTokens);
            deserialized.ReasoningTokens.Should().Be(original.ReasoningTokens);
            deserialized.CacheReadTokens.Should().Be(original.CacheReadTokens);
            deserialized.CacheWriteTokens.Should().Be(original.CacheWriteTokens);
            // IgnoreMember property should still compute correctly
            deserialized.TotalTokens.Should().Be(
                original.InputTokens + original.OutputTokens + original.ReasoningTokens);
        });
    }

    // ── Edge cases ───────────────────────────────────────────────────────

    [Fact]
    public void ConsolidationJobResult_WithHarnessSuggestions_SurvivesRoundTrip()
    {
        var original = new ConsolidationJobResult
        {
            JobId = "consolidation-123",
            Success = true,
            Summary = "Generated 3 suggestions",
            HarnessSuggestions = new HarnessSuggestions
            {
                BasedOnRunCount = 50,
                GeneratedAtUtc = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                SuccessRate = 0.72m,
                Suggestions =
                [
                    new HarnessSuggestion { Frequency = 15, Text = "Add DB schema", Rationale = "Frequent request" }
                ]
            }
        };

        var deserialized = RoundTrip(original);

        deserialized.HarnessSuggestions.Should().NotBeNull();
        deserialized.HarnessSuggestions!.BasedOnRunCount.Should().Be(50);
        deserialized.HarnessSuggestions.Suggestions.Should().HaveCount(1);
        deserialized.HarnessSuggestions.Suggestions[0].Text.Should().Be("Add DB schema");
    }

    [Fact]
    public void DecompositionProjectContext_EmptyRepositories_SurvivesRoundTrip()
    {
        var original = new DecompositionProjectContext
        {
            ProjectName = "EmptyProject",
            Repositories = []
        };

        var deserialized = RoundTrip(original);

        deserialized.ProjectName.Should().Be("EmptyProject");
        deserialized.Repositories.Should().BeEmpty();
    }

    [Fact]
    public void CreatedIssueInfo_RoundTrip_PreservesAllFields()
    {
        var original = new CreatedIssueInfo
        {
            Identifier = "42",
            Title = "Extract payment service",
            Url = "https://github.com/org/repo/issues/42"
        };

        var deserialized = RoundTrip(original);

        deserialized.Identifier.Should().Be("42");
        deserialized.Title.Should().Be("Extract payment service");
        deserialized.Url.Should().Be("https://github.com/org/repo/issues/42");
    }
}
