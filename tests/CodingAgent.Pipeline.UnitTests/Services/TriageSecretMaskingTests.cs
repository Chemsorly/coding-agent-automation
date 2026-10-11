using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary><see cref="TriageSecretMasking"/>: which MCP values count as credentials, and masking every text field.</summary>
public sealed class TriageSecretMaskingTests
{
    [Fact]
    public void McpSecrets_TakesEveryHeader_TheTokenAfterAnAuthScheme_AndSecretLookingEnvValues()
    {
        var secrets = TriageSecretMasking.McpSecrets(
        [
            new McpServerConfig
            {
                Name = "grafana",
                Type = "http",
                Headers = new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer glsa_abc123",
                    ["X-Org"] = "org-7",
                    ["X-Empty"] = " ",
                },
            },
            new McpServerConfig
            {
                Name = "db",
                Env = new Dictionary<string, string>
                {
                    ["DB_PASSWORD"] = "pw-123",
                    ["SONAR_TOKEN"] = "squ_456",
                    ["OPENAI_API_KEY"] = "sk-789",
                    ["LOG_LEVEL"] = "debug",
                    ["BASE_URL"] = "https://db.internal",
                    ["SESSION_COOKIE"] = "",
                },
            },
        ]);

        secrets.Values.Should().BeEquivalentTo("Bearer glsa_abc123", "glsa_abc123", "org-7", "pw-123", "squ_456", "sk-789");
    }

    [Fact]
    public void McpSecrets_NoServers_IsEmpty()
    {
        TriageSecretMasking.McpSecrets(null).Should().BeEmpty();
        TriageSecretMasking.McpSecrets([]).Should().BeEmpty();
    }

    [Fact]
    public void Mask_ReplacesTheSecretInEveryTextField()
    {
        const string Secret = "hunter2-secret";
        string S(string label) => $"{label} {Secret}";
        var result = new TriageResult
        {
            Verdict = TriageVerdict.CauseFound,
            Summary = S("summary"),
            Impact = S("impact"),
            Reproduction = S("repro"),
            DuplicateOf = S("dup"),
            CausalChain = [new TriageChainStep { Text = S("chain") }],
            Hypotheses = [new TriageHypothesis { Id = "H1", Text = S("hypothesis") }],
            Evidence = [new TriageEvidence { Id = "E1", Claim = S("claim"), Source = S("source"), Query = S("query"), Link = S("link") }],
            Investigated = [new TriageCheck { Check = S("check"), Where = S("where"), For = S("for"), Result = S("result") }],
            NotChecked = [new TriageGap { What = S("what"), Why = S("why") }],
            Questions = [new TriageQuestion { Question = S("question"), Why = S("qwhy") }],
            Drafts = [new TriageDraft { Id = "d1", TargetRepository = "api", Title = S("title"), Body = S("body"), Size = S("size"), Warning = S("warning") }],
        };

        var masked = TriageSecretMasking.Mask(result, [new KeyValuePair<string, string>("k", Secret)]);

        System.Text.Json.JsonSerializer.Serialize(masked).Should().NotContain(Secret);
        masked.Summary.Should().Be("summary ***");
        masked.Drafts[0].Body.Should().Be("body ***");
        masked.Evidence[0].Query.Should().Be("query ***");
        masked.Drafts[0].TargetRepository.Should().Be("api", "identifiers are not text the agent wrote about the problem");
    }

    [Fact]
    public void Mask_WithoutSecrets_ReturnsTheResultUnchanged()
    {
        var result = new TriageResult { Verdict = TriageVerdict.NotABug, Summary = "s" };

        TriageSecretMasking.Mask(result, []).Should().BeSameAs(result);
    }
}
