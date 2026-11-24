using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using MessagePack;
using MessagePack.Resolvers;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Spec 053 task 2.1/2.3: the routing types and the new fields survive the JSON the API stores (routing table,
/// template rows, work item payload) and the MessagePack the hub sends (templates, profiles).
/// </summary>
public class AgentRoutingSerializationTests
{
    private static readonly MessagePackSerializerOptions MsgPackOptions = ContractlessStandardResolverAllowPrivate.Options;

    private static readonly AgentRouting Routing = AgentRoutingTestData.Routing(
        "profile-kiro", "claude-sonnet-4-6", "high",
        (WorkItemTaskType.Decomposition, AgentRoutingTestData.Route("profile-claude", "opus", "xhigh")),
        (WorkItemTaskType.Triage, AgentRoutingTestData.Route("profile-claude", "auto", "auto")));

    [Fact]
    public void AgentRouting_JsonRoundTrip_KeepsTheDefaultAndTheTaskRows()
    {
        var json = JsonSerializer.Serialize(Routing, PipelineJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<AgentRouting>(json, PipelineJsonOptions.Lenient)!;

        restored.Default.Should().Be(Routing.Default);
        restored.ByTaskType.Should().BeEquivalentTo(Routing.ByTaskType);
    }

    [Fact]
    public void AgentRouting_Json_UsesTaskTypeNamesAsKeys()
    {
        var json = JsonSerializer.Serialize(Routing, PipelineJsonOptions.Default);

        json.Should().Contain("\"Decomposition\"").And.Contain("\"byTaskType\"").And.Contain("\"profileId\"");
    }

    [Fact]
    public void AgentRouting_JsonWithANumericTaskTypeKey_DeserializesTheValue_SoTheValidatorCanRefuseIt()
    {
        const string json = """{"default":{"profileId":"p","model":"auto","effort":"auto"},"byTaskType":{"99":{"profileId":"p","model":"auto","effort":"auto"}}}""";

        var restored = JsonSerializer.Deserialize<AgentRouting>(json, PipelineJsonOptions.Lenient)!;

        restored.ByTaskType.Keys.Should().ContainSingle().Which.Should().Be((WorkItemTaskType)99);
    }

    [Fact]
    public void AgentRouteSnapshot_Json_WritesTheSourceAsAName()
    {
        var snapshot = new AgentRouteSnapshot("p", "Kiro", "kiro", "auto", "high", AgentRouteSource.GlobalDefault);

        var json = JsonSerializer.Serialize(snapshot, PipelineJsonOptions.Default);

        json.Should().Contain("\"GlobalDefault\"");
        JsonSerializer.Deserialize<AgentRouteSnapshot>(json, PipelineJsonOptions.Lenient).Should().Be(snapshot);
    }

    [Fact]
    public void AgentRouteSnapshot_Json_KeepsTheLightFields()
    {
        var snapshot = new AgentRouteSnapshot("p", "Kiro", "kiro", "auto", "high", AgentRouteSource.GlobalDefault, "haiku", "low");

        var json = JsonSerializer.Serialize(snapshot, PipelineJsonOptions.Default);

        json.Should().Contain("\"lightModel\": \"haiku\"").And.Contain("\"lightEffort\": \"low\"");
        JsonSerializer.Deserialize<AgentRouteSnapshot>(json, PipelineJsonOptions.Lenient).Should().Be(snapshot);
    }

    [Fact]
    public void AgentRouteSnapshot_JsonWithoutLightFields_HasNullLightFields()
    {
        const string json = """{"profileId":"p","profileName":"Kiro","tool":"kiro","model":"auto","effort":"high","source":"GlobalDefault"}""";

        var snapshot = JsonSerializer.Deserialize<AgentRouteSnapshot>(json, PipelineJsonOptions.Lenient)!;

        snapshot.LightModel.Should().BeNull();
        snapshot.LightEffort.Should().BeNull();
        snapshot.Model.Should().Be("auto");
    }

    [Fact]
    public void AgentRoute_JsonWithoutLightFields_HasNullLightFields()
    {
        var routing = JsonSerializer.Deserialize<AgentRouting>(
            """{"default":{"profileId":"p","model":"auto","effort":"auto"}}""", PipelineJsonOptions.Lenient)!;

        routing.Default!.LightModel.Should().BeNull();
        routing.Default.LightEffort.Should().BeNull();
    }

    [Fact]
    public void AgentRoute_LightFields_SurviveJsonAndMessagePack()
    {
        var route = AgentRoutingTestData.Route("profile-claude", "opus", "high") with { LightModel = "haiku", LightEffort = "low" };

        var fromJson = JsonSerializer.Deserialize<AgentRoute>(
            JsonSerializer.Serialize(route, PipelineJsonOptions.Default), PipelineJsonOptions.Lenient);
        var fromMessagePack = MessagePackSerializer.Deserialize<AgentRoute>(
            MessagePackSerializer.Serialize(route, MsgPackOptions), MsgPackOptions);

        fromJson.Should().Be(route);
        fromMessagePack.Should().Be(route);
    }

    [Fact]
    public void AgentRoute_MessagePack_WritesTheLightFieldsAtKeys3And4()
    {
        var route = AgentRoutingTestData.Route("profile-claude", "opus", "high") with { LightModel = "haiku", LightEffort = "low" };

        var reader = new MessagePackReader(MessagePackSerializer.Serialize(route, MsgPackOptions));

        reader.ReadArrayHeader().Should().Be(5);
        new[] { reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString() }
            .Should().Equal("profile-claude", "opus", "high", "haiku", "low");
    }

    [Fact]
    public void AgentRoute_ThreeElementMessagePack_HasNullLightFields()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(3);
        writer.Write("profile-claude");
        writer.Write("opus");
        writer.Write("high");
        writer.Flush();

        var route = MessagePackSerializer.Deserialize<AgentRoute>(buffer.WrittenMemory, MsgPackOptions);

        route.Should().Be(AgentRoutingTestData.Route("profile-claude", "opus", "high"));
        route.LightModel.Should().BeNull();
        route.LightEffort.Should().BeNull();
    }

    [Fact]
    public void PipelineJobTemplate_AgentRoutesWithLightFields_SurviveJsonAndMessagePack()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t", Name = "api", IssueProviderId = "ip", RepoProviderId = "rp",
            AgentRoutes = new Dictionary<WorkItemTaskType, AgentRoute>
            {
                [WorkItemTaskType.Implementation] =
                    AgentRoutingTestData.Route("profile-claude", "opus", "high") with { LightModel = "haiku", LightEffort = "low" },
            },
        };

        var fromJson = JsonSerializer.Deserialize<PipelineJobTemplate>(
            JsonSerializer.Serialize(template, PipelineJsonOptions.Default), PipelineJsonOptions.Lenient)!;
        var fromMessagePack = MessagePackSerializer.Deserialize<PipelineJobTemplate>(
            MessagePackSerializer.Serialize(template, MsgPackOptions), MsgPackOptions);

        fromJson.AgentRoutes.Should().BeEquivalentTo(template.AgentRoutes);
        fromMessagePack.AgentRoutes.Should().BeEquivalentTo(template.AgentRoutes);
    }

    [Fact]
    public void JobDistributionRequest_JsonRoundTrip_KeepsTheRoute_Req4_4()
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = "42",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Decomposition,
            AgentSelector = "claude,dotnet",
            TimeoutSeconds = 60,
            Route = new AgentRouteSnapshot("profile-claude", "Claude", "claude", "opus", "xhigh", AgentRouteSource.GlobalTaskRow),
        };

        var json = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<JobDistributionRequest>(json, PipelineJsonOptions.Lenient)!;

        restored.Route.Should().Be(request.Route);
    }

    [Fact]
    public void PipelineJobTemplate_AgentRoutes_SurviveJsonAndMessagePack_Req3_1()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t", Name = "api", IssueProviderId = "ip", RepoProviderId = "rp",
            AgentRoutes = new Dictionary<WorkItemTaskType, AgentRoute>
            {
                [WorkItemTaskType.Implementation] = AgentRoutingTestData.Route("profile-claude", "opus", "high"),
            },
        };

        var fromJson = JsonSerializer.Deserialize<PipelineJobTemplate>(
            JsonSerializer.Serialize(template, PipelineJsonOptions.Default), PipelineJsonOptions.Lenient)!;
        var fromMessagePack = MessagePackSerializer.Deserialize<PipelineJobTemplate>(
            MessagePackSerializer.Serialize(template, MsgPackOptions), MsgPackOptions);

        fromJson.AgentRoutes.Should().BeEquivalentTo(template.AgentRoutes);
        fromMessagePack.AgentRoutes.Should().BeEquivalentTo(template.AgentRoutes);
    }

    [Fact]
    public void PipelineJobTemplate_WithoutAgentRoutes_HasNullRows()
    {
        var template = JsonSerializer.Deserialize<PipelineJobTemplate>(
            """{"id":"t","name":"api","issueProviderId":"ip","repoProviderId":"rp"}""", PipelineJsonOptions.Lenient)!;

        template.AgentRoutes.Should().BeNull("null means no rows stored, and in a save body 'keep the stored rows'");
    }

    [Fact]
    public void AgentProfile_Settings_SurviveJsonAndMessagePack_Req1_1()
    {
        var profile = AgentRoutingTestData.Profile(
            "profile-claude", "claude,dotnet", "Claude",
            settings: new Dictionary<string, string> { [ProviderSettingKeys.AuthMode] = ClaudeCodeAuthModes.Subscription });

        var fromJson = JsonSerializer.Deserialize<AgentProfile>(
            JsonSerializer.Serialize(profile, PipelineJsonOptions.Default), PipelineJsonOptions.Lenient)!;
        var fromMessagePack = MessagePackSerializer.Deserialize<AgentProfile>(
            MessagePackSerializer.Serialize(profile, MsgPackOptions), MsgPackOptions);

        fromJson.Settings.Should().BeEquivalentTo(profile.Settings);
        fromMessagePack.Settings.Should().BeEquivalentTo(profile.Settings);
    }

    [Fact]
    public void AgentProfile_InTheSpec053Shape_Deserializes()
    {
        // Review-3 M2: a converted profile has no provider config, labels or priority; it is read and written as is.
        var profile = JsonSerializer.Deserialize<AgentProfile>(
            """{"id":"p","displayName":"Kiro","jobTemplate":"dotnet,kiro","settings":{}}""", PipelineJsonOptions.Lenient)!;

        profile.JobTemplateKey().Should().Be("dotnet,kiro");
        profile.Settings.Should().BeEmpty();
    }

    [Fact]
    public void AgentProfile_WithoutSettings_HasNullSettings_SoAStaleFormIsDetectable()
    {
        var profile = JsonSerializer.Deserialize<AgentProfile>(
            """{"id":"p","displayName":"Kiro","agentProviderConfigId":"a"}""", PipelineJsonOptions.Lenient)!;

        profile.Settings.Should().BeNull();
    }
}
