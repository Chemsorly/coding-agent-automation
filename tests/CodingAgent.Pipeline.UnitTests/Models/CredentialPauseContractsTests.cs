using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using MessagePack;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>Spec 056 req 1.1, D9, design DD4: one function maps a tool and a profile to its credential.</summary>
public class AgentCredentialsTests
{
    private static Dictionary<string, string> AuthMode(string value) => new() { [ProviderSettingKeys.AuthMode] = value };

    [Theory]
    [InlineData("kiro", AgentCredentials.Kiro)]
    [InlineData("KIRO", AgentCredentials.Kiro)]
    [InlineData("KiroCli", AgentCredentials.Kiro)]
    [InlineData("opencode", AgentCredentials.Opencode)]
    [InlineData("claude", AgentCredentials.ClaudeSubscription)]
    public void For_WhenToolIsKnown_ShouldMapToItsCredential(string tool, string expected) =>
        AgentCredentials.For(tool, settings: null).Should().Be(expected);

    [Theory]
    [InlineData("apiKey", AgentCredentials.ClaudeApiKey)]
    [InlineData("APIKEY", AgentCredentials.ClaudeApiKey)]
    [InlineData("subscription", AgentCredentials.ClaudeSubscription)]
    [InlineData("auto", AgentCredentials.ClaudeSubscription)]
    [InlineData("", AgentCredentials.ClaudeSubscription)]
    [InlineData("something-else", AgentCredentials.ClaudeSubscription)]
    public void For_WhenClaude_ShouldUseTheApiKeyOnlyForAuthModeApiKey_D9(string authMode, string expected) =>
        AgentCredentials.For("claude", AuthMode(authMode)).Should().Be(expected);

    [Fact]
    public void For_WhenClaudeSettingsLackAuthMode_ShouldBeTheSubscription() =>
        AgentCredentials.For("claude", new Dictionary<string, string>()).Should().Be(AgentCredentials.ClaudeSubscription);

    [Fact]
    public void For_WhenKiroHasAnAuthModeSetting_ShouldIgnoreIt() =>
        AgentCredentials.For("kiro", AuthMode("apiKey")).Should().Be(AgentCredentials.Kiro);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("codex")]
    public void For_WhenToolIsEmptyOrUnknown_ShouldBeNull(string? tool) =>
        AgentCredentials.For(tool, settings: null).Should().BeNull();

    [Fact]
    public void For_Profile_ShouldUseItsJobTemplatesTool()
    {
        var tools = new Dictionary<string, string> { ["claude,dotnet"] = "claude" };
        var profile = new AgentProfile { DisplayName = "C", JobTemplate = "dotnet, claude", Settings = AuthMode("apiKey") };

        AgentCredentials.For(profile, tools).Should().Be(AgentCredentials.ClaudeApiKey);
        AgentCredentials.For(profile with { JobTemplate = "missing" }, tools).Should().BeNull();
    }

    [Fact]
    public void InUse_ShouldListEachCredentialOnceInTheOrderOfAll()
    {
        var tools = new Dictionary<string, string> { ["kiro"] = "kiro", ["opencode"] = "opencode", ["claude"] = "claude", ["none"] = "" };
        var profiles = new[]
        {
            new AgentProfile { DisplayName = "O", JobTemplate = "opencode" },
            new AgentProfile { DisplayName = "K1", JobTemplate = "kiro" },
            new AgentProfile { DisplayName = "K2", JobTemplate = "kiro" },
            new AgentProfile { DisplayName = "C", JobTemplate = "claude", Settings = AuthMode("subscription") },
            new AgentProfile { DisplayName = "N", JobTemplate = "none" },
            new AgentProfile { DisplayName = "M", JobTemplate = "missing" },
        };

        AgentCredentials.InUse(profiles, tools).Should().Equal(
            AgentCredentials.Kiro, AgentCredentials.ClaudeSubscription, AgentCredentials.Opencode);
    }

    [Fact]
    public void All_ShouldHaveADisplayNameForEveryCredential()
    {
        foreach (var credential in AgentCredentials.All)
            AgentCredentials.DisplayName(credential).Should().NotBe(credential);
        AgentCredentials.IsKnown("kiro").Should().BeTrue();
        AgentCredentials.IsKnown("Kiro").Should().BeFalse();
        AgentCredentials.IsKnown(null).Should().BeFalse();
    }
}

/// <summary>Spec 056 req 2.5, design DD6: how a new pause combines with the stored one.</summary>
public class CredentialPauseRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 6, 26, 0, TimeSpan.Zero);

    private static AgentCredentialPause Manual(DateTimeOffset? until = null) => new(
        AgentCredentials.Kiro, CredentialPauseReasons.Manual, CredentialPauseSources.Manual, Now, until, "note", PausedBy: "alice");

    private static AgentCredentialPause Auto(DateTimeOffset? until, string detail = "auto") => new(
        AgentCredentials.Kiro, CredentialPauseReasons.QuotaExhausted, CredentialPauseSources.Auto, Now, until, detail);

    [Fact]
    public void Apply_ManualOverNone_ShouldStoreIt() =>
        CredentialPauseRules.Apply(null, Manual(), Now).Should().Be(Manual());

    [Fact]
    public void Apply_ManualOverAuto_ShouldReplaceIt() =>
        CredentialPauseRules.Apply(Auto(Now.AddDays(3)), Manual(), Now).Should().Be(Manual());

    [Fact]
    public void Apply_ManualOverManual_ShouldReplaceIt() =>
        CredentialPauseRules.Apply(Manual(), Manual(Now.AddHours(1)), Now).Should().Be(Manual(Now.AddHours(1)));

    [Fact]
    public void Apply_AutoOverNone_ShouldStoreIt() =>
        CredentialPauseRules.Apply(null, Auto(Now.AddDays(1)), Now).Should().Be(Auto(Now.AddDays(1)));

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void Apply_AutoOverManual_ShouldChangeNothing(int? untilDays) =>
        CredentialPauseRules.Apply(Manual(), Auto(untilDays is { } d ? Now.AddDays(d) : null), Now).Should().BeNull();

    [Fact]
    public void Apply_AutoOverAutoWithALaterUntil_ShouldExtendTheStoredEntry()
    {
        var stored = Auto(Now.AddDays(1), "first");

        var result = CredentialPauseRules.Apply(stored, Auto(Now.AddDays(5), "second"), Now);

        result.Should().Be(stored with { Until = Now.AddDays(5) });
    }

    [Fact]
    public void Apply_AutoOverAutoWithAnEarlierUntil_ShouldChangeNothing() =>
        CredentialPauseRules.Apply(Auto(Now.AddDays(5)), Auto(Now.AddDays(1)), Now).Should().BeNull();

    [Fact]
    public void Apply_AutoWithoutUntilOverAutoWithUntil_ShouldPauseUntilResumed() =>
        CredentialPauseRules.Apply(Auto(Now.AddDays(1), "first"), Auto(null), Now)
            .Should().Be(Auto(null, "first"));

    [Fact]
    public void Apply_AutoOverAutoWithoutUntil_ShouldChangeNothing() =>
        CredentialPauseRules.Apply(Auto(null), Auto(Now.AddDays(9)), Now).Should().BeNull();

    [Theory]
    [InlineData(CredentialPauseSources.Auto)]
    [InlineData(CredentialPauseSources.Manual)]
    public void Apply_AutoOverAnExpiredEntry_ShouldStoreTheNewOne(string expiredSource)
    {
        var expired = (expiredSource == CredentialPauseSources.Manual ? Manual(Now.AddMinutes(-1)) : Auto(Now.AddMinutes(-1)));

        CredentialPauseRules.Apply(expired, Auto(Now.AddDays(1), "new"), Now).Should().Be(Auto(Now.AddDays(1), "new"));
    }

    [Fact]
    public void IsActive_ShouldBeTrueUntilTheEndTime()
    {
        Auto(null).IsActive(Now).Should().BeTrue();
        Auto(Now.AddSeconds(1)).IsActive(Now).Should().BeTrue();
        Auto(Now).IsActive(Now).Should().BeFalse();
    }
}

/// <summary>Spec 056 DD11: the provider failure on the completion payloads is additive on both wires.</summary>
public class AgentProviderFailureWireTests
{
    private static readonly AgentProviderFailure Failure = new(
        AgentProviderFailure.QuotaExhausted, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), "monthly usage limit");

    private static JobCompletionPayload Payload(AgentProviderFailure? failure) => new()
    {
        FinalStep = PipelineStep.Failed,
        CompletedAt = new DateTimeOffset(2026, 11, 2, 6, 26, 0, TimeSpan.Zero),
        ProviderFailure = failure,
    };

    [Fact]
    public void JobCompletionPayload_MessagePack_ShouldRoundTripTheFailure()
    {
        var bytes = MessagePackSerializer.Serialize(Payload(Failure), AgentHubMessagePack.SerializerOptions);

        MessagePackSerializer.Deserialize<JobCompletionPayload>(bytes, AgentHubMessagePack.SerializerOptions)
            .ProviderFailure.Should().Be(Failure);
    }

    [Fact]
    public void JobCompletionPayload_Json_ShouldRoundTripTheFailureInCamelCase()
    {
        var json = JsonSerializer.Serialize(Payload(Failure), PipelineJsonOptions.Default);

        json.Should().Contain("\"providerFailure\"").And.Contain("\"category\": \"quota_exhausted\"");
        JsonSerializer.Deserialize<JobCompletionPayload>(json, PipelineJsonOptions.Lenient)!
            .ProviderFailure.Should().Be(Failure);
    }

    [Fact]
    public void JobCompletionPayload_FromAnOlderAgent_ShouldHaveNoFailure()
    {
        // An old agent writes 36 keys (0..35); the reader must leave key 36 null.
        var bytes = MessagePackSerializer.Serialize(Payload(null), AgentHubMessagePack.SerializerOptions);
        MessagePackSerializer.Deserialize<JobCompletionPayload>(bytes, AgentHubMessagePack.SerializerOptions)
            .ProviderFailure.Should().BeNull();

        const string oldJson = """{"finalStep":"Failed","completedAt":"2026-11-02T06:26:00+00:00"}""";
        JsonSerializer.Deserialize<JobCompletionPayload>(oldJson, PipelineJsonOptions.Lenient)!
            .ProviderFailure.Should().BeNull();
    }

    [Fact]
    public void ConsolidationJobResult_MessagePack_ShouldRoundTripTheFailure()
    {
        var result = new ConsolidationJobResult { JobId = "job-1", Success = false, ProviderFailure = Failure };

        var bytes = MessagePackSerializer.Serialize(result, AgentHubMessagePack.SerializerOptions);

        MessagePackSerializer.Deserialize<ConsolidationJobResult>(bytes, AgentHubMessagePack.SerializerOptions)
            .ProviderFailure.Should().Be(Failure);
    }

    [Fact]
    public void ConsolidationJobResult_FromAnOlderAgent_ShouldHaveNoFailure()
    {
        var bytes = MessagePackSerializer.Serialize(
            new ConsolidationJobResult { JobId = "job-1", Success = false }, AgentHubMessagePack.SerializerOptions);

        MessagePackSerializer.Deserialize<ConsolidationJobResult>(bytes, AgentHubMessagePack.SerializerOptions)
            .ProviderFailure.Should().BeNull();
    }

    [Fact]
    public void Create_ShouldCutTheDetailTo300Characters()
    {
        var failure = AgentProviderFailure.Create(AgentProviderFailure.AuthFailed, null, new string('x', 400));

        failure.Detail.Should().HaveLength(AgentProviderFailure.MaxDetailLength);
        AgentProviderFailure.Create(AgentProviderFailure.AuthFailed, null, null).Detail.Should().BeEmpty();
    }
}

/// <summary>
/// Spec 056 "Enums on the wire or in the database": the numeric values never change. ErrorCategory 5 was
/// ProviderBillingLimit and is ProviderQuotaExhausted now; ProviderUnavailable is appended to FailureReason.
/// </summary>
public class CredentialPauseEnumValueTests
{
    [Fact]
    public void AgentErrorCategory_ShouldKeepItsValues()
    {
        ((int)AgentErrorCategory.None).Should().Be(0);
        ((int)AgentErrorCategory.ProviderRateLimit).Should().Be(1);
        ((int)AgentErrorCategory.ProviderOverload).Should().Be(2);
        ((int)AgentErrorCategory.PermanentAuthFailure).Should().Be(3);
        ((int)AgentErrorCategory.ProviderMisconfigured).Should().Be(4);
        ((int)AgentErrorCategory.ProviderQuotaExhausted).Should().Be(5);
    }

    [Fact]
    public void FailureReason_ShouldKeepItsValuesAndAppendProviderUnavailable()
    {
        ((int)FailureReason.Timeout).Should().Be(0);
        ((int)FailureReason.InfrastructureFailure).Should().Be(1);
        ((int)FailureReason.AgentError).Should().Be(2);
        ((int)FailureReason.TokenRefreshFailure).Should().Be(3);
        ((int)FailureReason.ExitCodeFailure).Should().Be(4);
        ((int)FailureReason.QualityGateExhausted).Should().Be(5);
        ((int)FailureReason.GateRejected).Should().Be(6);
        ((int)FailureReason.ProviderUnavailable).Should().Be(7);
    }

    [Fact]
    public void ProviderCredentialMissingException_ShouldNameTheCredential()
    {
        var ex = new ProviderCredentialMissingException(AgentCredentials.ClaudeSubscription);

        ex.Credential.Should().Be(AgentCredentials.ClaudeSubscription);
        ex.Message.Should().Be("No Claude subscription is configured");
    }
}

/// <summary>Spec 056 design "Hold Reasons and Texts", DD10, DD11 step 3.</summary>
public class WorkItemHoldsTests
{
    [Fact]
    public void OperatorHolds_ShouldBeTheFiveReasonsOnlyAnOperatorEnds_DD10()
    {
        HoldReasons.OperatorHolds.Should().BeEquivalentTo(
            ["credential_paused", "no_route", "profile_unavailable", "no_tool", "no_template"]);
        HoldReasons.All.Should().HaveCount(8).And.Contain(HoldReasons.OperatorHolds);
        HoldReasons.IsOperatorHold("concurrency_limit").Should().BeFalse();
        HoldReasons.IsOperatorHold("pvc_unavailable").Should().BeFalse();
        HoldReasons.IsOperatorHold(null).Should().BeFalse();
        HoldReasons.IsOperatorHold("no_route").Should().BeTrue();
    }

    [Fact]
    public void CredentialPaused_WhenAutomatic_ShouldNameCredentialReasonSinceAndUntil()
    {
        var pause = new AgentCredentialPause(
            AgentCredentials.Kiro, CredentialPauseReasons.QuotaExhausted, CredentialPauseSources.Auto,
            new DateTimeOffset(2026, 11, 2, 6, 26, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), "x");

        HoldTexts.CredentialPaused(pause).Should().Be(
            "Kiro paused — quota used up, since 2026-11-02 06:26 UTC (until 2026-12-01 00:00 UTC)");
    }

    [Fact]
    public void CredentialPaused_WhenManualWithoutUntil_ShouldNameTheUser()
    {
        var pause = new AgentCredentialPause(
            AgentCredentials.ClaudeApiKey, CredentialPauseReasons.Manual, CredentialPauseSources.Manual,
            new DateTimeOffset(2026, 11, 2, 6, 26, 0, TimeSpan.Zero), null, "", PausedBy: "alice");

        HoldTexts.CredentialPaused(pause).Should().Be("Claude API key paused — paused by alice, since 2026-11-02 06:26 UTC");
    }

    [Fact]
    public void NoRoute_ShouldNameTheTaskTypeAndTheFix() =>
        HoldTexts.NoRoute(WorkItemTaskType.Triage).Should().Be("No agent route for Triage — set it in Settings → Agents → Routing");

    [Theory]
    [InlineData(LimitScope.Template, LimitSource.Ui, "4 of 4 agents are running for dotnet,kiro (limit set in Settings)")]
    [InlineData(LimitScope.Template, LimitSource.Helm, "4 of 4 agents are running for dotnet,kiro (limit set in the Helm chart)")]
    [InlineData(LimitScope.Global, LimitSource.Helm, "4 of 4 agents are running (limit set in the Helm chart)")]
    [InlineData(LimitScope.Global, LimitSource.Ui, "4 of 4 agents are running (limit set in Settings)")]
    public void Limit_ShouldFollowSpec057Wording_D15(LimitScope scope, LimitSource source, string expected) =>
        HoldTexts.Limit(new AgentStartDecision(false, scope, source, 4, 4), "dotnet,kiro").Should().Be(expected);

    [Fact]
    public void Cut_ShouldKeepAtMost500Characters()
    {
        HoldTexts.Cut(new string('a', 600)).Should().HaveLength(HoldTexts.MaxLength);
        HoldTexts.Cut("short").Should().Be("short");
    }

    [Theory]
    [InlineData(PipelineRunType.Implementation, "agent:next")]
    [InlineData(PipelineRunType.Review, "agent:next")]
    [InlineData(PipelineRunType.DecompositionAnalysis, "agent:epic")]
    [InlineData(PipelineRunType.Decomposition, "agent:epic-approved")]
    [InlineData(PipelineRunType.Triage, "agent:triage")]
    [InlineData(PipelineRunType.Consolidation, null)]
    public void QueueLabels_For_ShouldBeTheLabelThatQueuesTheRunType(PipelineRunType runType, string? expected) =>
        QueueLabels.For(runType).Should().Be(expected);
}

/// <summary>Spec 056 req 3.6, design DD3: obsolete route sources still read from stored payloads.</summary>
public class ObsoleteRouteSourceTests
{
    [Theory]
    [InlineData("Dispatch")]
    [InlineData("Claim")]
    public void StoredRouteSnapshot_WithAnObsoleteSource_ShouldStillDeserialize(string source)
    {
        var json = $$"""{"profileId":"p","profileName":"P","tool":"kiro","model":"auto","effort":"auto","source":"{{source}}"}""";

        var snapshot = JsonSerializer.Deserialize<AgentRouteSnapshot>(json, PipelineJsonOptions.Lenient);

        snapshot!.Source.ToString().Should().Be(source);
        AgentRouteLabels.Source(snapshot.Source).Should().EndWith("(earlier release)");
    }
}
