using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using MessagePack;
using MessagePack.Resolvers;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// MessagePack serialization round-trip tests for DTOs not covered by
/// HubMessageRoundtripPropertyTests or FeedbackMessagePackRoundtripPropertyTests.
/// Covers: DecompositionProjectContext, RepositoryTarget, ConsolidationJobResult,
/// CreatedIssueInfo, HarnessSuggestions, HarnessSuggestion, TokenUsage.
/// </summary>
public class AdditionalMessagePackRoundtripPropertyTests
{
    private static readonly MessagePackSerializerOptions MsgPackOptions =
        ContractlessStandardResolverAllowPrivate.Options;

    private static T RoundTrip<T>(T original)
    {
        var bytes = MessagePackSerializer.Serialize(original, MsgPackOptions);
        return MessagePackSerializer.Deserialize<T>(bytes, MsgPackOptions);
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

    [Property(MaxTest = 30)]
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
