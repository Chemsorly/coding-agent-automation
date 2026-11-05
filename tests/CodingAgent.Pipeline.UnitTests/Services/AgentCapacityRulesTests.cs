using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>Spec 057 req 1.5, 1.6, 3.1, 4.1, 10.1: the four limits and the decisions for work items and chats.</summary>
public sealed class AgentCapacityRulesTests
{
    private const string Kiro = "dotnet,dotnet10,kiro";
    private const string Claude = "claude,dotnet,dotnet10";

    private static AgentCapacityInputs Inputs(
        int helmGlobal = 0,
        int uiGlobal = 0,
        Dictionary<string, int>? helm = null,
        Dictionary<string, int>? ui = null,
        Dictionary<string, int>? running = null,
        int? total = null)
    {
        running ??= new Dictionary<string, int>(StringComparer.Ordinal);
        return new AgentCapacityInputs(
            helmGlobal,
            new AgentCapacityLimits
            {
                MaxConcurrentAgents = uiGlobal,
                MaxConcurrentByJobTemplate = ui ?? new Dictionary<string, int>(StringComparer.Ordinal)
            },
            helm ?? new Dictionary<string, int>(StringComparer.Ordinal) { [Kiro] = 0, [Claude] = 0 },
            running,
            total ?? running.Values.Sum());
    }

    // req 1.5, 1.6
    [Theory]
    [InlineData(0, 0, null, null)]
    [InlineData(4, 0, 4, LimitSource.Helm)]
    [InlineData(0, 3, 3, LimitSource.Ui)]
    [InlineData(4, 3, 3, LimitSource.Ui)]
    [InlineData(3, 4, 3, LimitSource.Helm)]
    [InlineData(4, 4, 4, LimitSource.Helm)]
    [InlineData(-1, 2, 2, LimitSource.Ui)]
    [InlineData(2, -5, 2, LimitSource.Helm)]
    public void Effective_ShouldPickTheLowestSetLimit_HelmWinningATie(int helm, int ui, int? value, LimitSource? source)
    {
        var effective = AgentCapacityRules.Effective(helm, ui);

        if (value is null)
        {
            effective.Should().BeNull();
            return;
        }

        effective.Should().Be(new EffectiveLimit(value.Value, source!.Value));
    }

    // req 3.1: the global limit is checked first and wins even when the template is also full.
    [Fact]
    public void CheckWorkItem_WhenTheGlobalLimitIsReached_ShouldHoldWithTheGlobalReason()
    {
        var inputs = Inputs(helmGlobal: 6, uiGlobal: 4,
            helm: new() { [Kiro] = 4, [Claude] = 0 },
            running: new() { [Kiro] = 4 });

        var decision = AgentCapacityRules.CheckWorkItem(inputs, Kiro);

        decision.Should().Be(new AgentStartDecision(false, LimitScope.Global, LimitSource.Ui, 4, 4));
        decision.Reason.Should().Be("global_concurrency_limit");
    }

    // req 3.1
    [Fact]
    public void CheckWorkItem_WhenTheTemplateLimitIsReached_ShouldHoldWithTheTemplateReason()
    {
        var inputs = Inputs(helm: new() { [Kiro] = 4, [Claude] = 0 }, running: new() { [Kiro] = 4, [Claude] = 1 });

        var decision = AgentCapacityRules.CheckWorkItem(inputs, Kiro);

        decision.Should().Be(new AgentStartDecision(false, LimitScope.Template, LimitSource.Helm, 4, 4));
        decision.Reason.Should().Be("concurrency_limit");
    }

    [Fact]
    public void CheckWorkItem_WhenBelowEveryLimit_ShouldAllow()
    {
        var inputs = Inputs(helmGlobal: 6, uiGlobal: 5,
            helm: new() { [Kiro] = 4, [Claude] = 0 }, ui: new() { [Kiro] = 3 },
            running: new() { [Kiro] = 2, [Claude] = 2 });

        var decision = AgentCapacityRules.CheckWorkItem(inputs, Kiro);

        decision.Allowed.Should().BeTrue();
        decision.Reason.Should().BeNull();
    }

    // req 1.5: a UI limit applies on a template without a Helm limit.
    [Fact]
    public void CheckWorkItem_WhenUiLimitsATemplateWithoutAHelmLimit_ShouldHoldAtTheUiLimit()
    {
        var inputs = Inputs(ui: new() { [Claude] = 2 }, running: new() { [Claude] = 2 });

        var decision = AgentCapacityRules.CheckWorkItem(inputs, Claude);

        decision.Should().Be(new AgentStartDecision(false, LimitScope.Template, LimitSource.Ui, 2, 2));
    }

    // req 1.6: a UI value above Helm is kept but Helm applies.
    [Fact]
    public void CheckWorkItem_WhenUiIsAboveHelm_ShouldHoldAtHelm()
    {
        var inputs = Inputs(helm: new() { [Kiro] = 3, [Claude] = 0 }, ui: new() { [Kiro] = 5 }, running: new() { [Kiro] = 3 });

        var decision = AgentCapacityRules.CheckWorkItem(inputs, Kiro);

        decision.Should().Be(new AgentStartDecision(false, LimitScope.Template, LimitSource.Helm, 3, 3));
    }

    // req 1.7: a UI entry for a key that is not deployed is ignored.
    [Fact]
    public void CheckWorkItem_WhenTheKeyIsNotDeployed_ShouldIgnoreItsUiLimit()
    {
        var inputs = Inputs(ui: new() { ["gone,old"] = 1 }, running: new() { ["gone,old"] = 5 });

        AgentCapacityRules.CheckWorkItem(inputs, "gone,old").Allowed.Should().BeTrue();
    }

    // req 2.1: Jobs of a template that is not deployed count toward the total only.
    [Fact]
    public void CheckWorkItem_WhenUnknownJobsRun_ShouldCountThemTowardTheTotalOnly()
    {
        var inputs = Inputs(helmGlobal: 3, helm: new() { [Kiro] = 2, [Claude] = 0 },
            running: new() { [Kiro] = 1 }, total: 3);

        var decision = AgentCapacityRules.CheckWorkItem(inputs, Claude);

        decision.Should().Be(new AgentStartDecision(false, LimitScope.Global, LimitSource.Helm, 3, 3));
    }

    // req 4.1, D5: UI limits never refuse a chat.
    [Fact]
    public void CheckChat_WhenOnlyUiLimitsAreReached_ShouldAllow()
    {
        var inputs = Inputs(uiGlobal: 1, ui: new() { [Kiro] = 1 }, running: new() { [Kiro] = 3 });

        AgentCapacityRules.CheckChat(inputs, Kiro).Allowed.Should().BeTrue();
    }

    // req 4.1
    [Fact]
    public void CheckChat_WhenTheHelmTemplateLimitIsReached_ShouldRefuse()
    {
        var inputs = Inputs(helm: new() { [Kiro] = 4, [Claude] = 0 }, running: new() { [Kiro] = 4 });

        AgentCapacityRules.CheckChat(inputs, Kiro)
            .Should().Be(new AgentStartDecision(false, LimitScope.Template, LimitSource.Helm, 4, 4));
    }

    // req 4.1
    [Fact]
    public void CheckChat_WhenTheHelmGlobalLimitIsReached_ShouldRefuse()
    {
        var inputs = Inputs(helmGlobal: 2, running: new() { [Claude] = 2 });

        AgentCapacityRules.CheckChat(inputs, Kiro)
            .Should().Be(new AgentStartDecision(false, LimitScope.Global, LimitSource.Helm, 2, 2));
    }

    // req 7.5: lowercase values for logs and tags.
    [Fact]
    public void TagValue_ShouldBeLowercase()
    {
        AgentCapacityRules.TagValue(LimitScope.Template).Should().Be("template");
        AgentCapacityRules.TagValue(LimitScope.Global).Should().Be("global");
        AgentCapacityRules.TagValue(LimitSource.Helm).Should().Be("helm");
        AgentCapacityRules.TagValue(LimitSource.Ui).Should().Be("ui");
    }
}
