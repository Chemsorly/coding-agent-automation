using AwesomeAssertions;
using Bunit;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Ui;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>Spec 057 req 6.1–6.4: Settings → Agents → Capacity.</summary>
public class AgentCapacitySectionComponentTests : BunitContext
{
    private readonly Mock<IUiSettingsClient> _settings = new();
    private readonly List<(string Message, bool IsError)> _statuses = [];
    private AgentCapacityLimits? _saved;

    private static UiAgentCapacityPage Page(bool countsAvailable = true) => new(
        new UiAgentCapacityRow(null, "", 3, 1, 6, 4, 4, "ui"),
        [
            new UiAgentCapacityRow("", "opencode", 0, 0, 0, 0, null, null),
            new UiAgentCapacityRow("claude", "claude", 1, 0, 0, 2, 2, "ui"),
            new UiAgentCapacityRow("dotnet,kiro", "kiro", 2, 1, 3, 5, 3, "helm"),
        ],
        0, 0, countsAvailable);

    public AgentCapacitySectionComponentTests()
    {
        _settings.Setup(s => s.GetAgentCapacityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Page());
        _settings.Setup(s => s.SaveAgentCapacityAsync(It.IsAny<AgentCapacityLimits>(), It.IsAny<CancellationToken>()))
            .Callback<AgentCapacityLimits, CancellationToken>((l, _) => _saved = l)
            .Returns(Task.CompletedTask);
    }

    private IRenderedComponent<AgentCapacitySection> RenderSection() =>
        Render<AgentCapacitySection>(p => p
            .Add(s => s.SettingsClient, _settings.Object)
            .Add(s => s.OnShowStatus, (ValueTuple<string, bool> s) => _statuses.Add(s)));

    // req 6.1
    [Fact]
    public void Rows_ShowRunningHelmUiAndWhatApplies()
    {
        var cut = RenderSection();

        var all = cut.Find("[data-testid='capacity-row-all']");
        all.TextContent.Should().Contain("All agents");
        all.QuerySelector("[data-testid='capacity-running']")!.TextContent.Should().Be("3 runs · 1 chat");
        all.QuerySelector("[data-testid='capacity-helm']")!.TextContent.Should().Be("6");
        all.QuerySelector("[data-testid='capacity-applies']")!.TextContent.Should().Contain("4 (UI)");

        var claude = cut.Find("[data-testid='capacity-row-claude']");
        claude.QuerySelector("[data-testid='capacity-running']")!.TextContent.Should().Be("1 run · 0 chats");
        claude.QuerySelector("[data-testid='capacity-helm']")!.TextContent.Should().Be("none");
        claude.QuerySelector("[data-testid='capacity-applies']")!.TextContent.Should().Contain("2 (UI)");

        cut.Find("[data-testid='capacity-row-']").TextContent.Should().Contain("(no labels)");
        cut.Find("[data-testid='capacity-row-'] [data-testid='capacity-applies']").TextContent.Trim().Should().Be("none");
    }

    // req 1.6
    [Fact]
    public void UiAboveHelm_IsShownAsCapped()
    {
        var cut = RenderSection();

        var kiro = cut.Find("[data-testid='capacity-row-dotnet,kiro']");
        kiro.QuerySelector("[data-testid='capacity-applies']")!.TextContent.Should().Contain("3 (Helm)");
        kiro.QuerySelector("[data-testid='capacity-capped']")!.TextContent.Should().Be("capped by the Helm chart at 3");
        cut.FindAll("[data-testid='capacity-capped']").Should().HaveCount(1);
    }

    // req 6.1: the inputs show the UI values, empty for none; no data-setting attribute anywhere.
    [Fact]
    public void Inputs_ShowTheUiValues_AndCarryNoDataSetting()
    {
        var cut = RenderSection();

        cut.Find("input[data-capacity-key='all']").GetAttribute("value").Should().Be("4");
        cut.Find("input[data-capacity-key='claude']").GetAttribute("value").Should().Be("2");
        cut.Find("input[data-capacity-key='']").GetAttribute("value").Should().BeEmpty();
        cut.FindAll("[data-setting]").Should().BeEmpty("SettingsCoverageTests requires data-setting to name PipelineConfiguration paths");
    }

    // req 6.2
    [Fact]
    public void HelpText_ExplainsThePrecedence()
    {
        RenderSection().Find("[data-testid='capacity-help']").TextContent.Should()
            .Contain("The lowest limit applies").And.Contain("only Helm limits refuse a chat").And.Contain("0 or empty means no limit");
    }

    // req 2.4
    [Fact]
    public void CountsUnavailable_ShowsTheBanner()
    {
        _settings.Setup(s => s.GetAgentCapacityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Page(countsAvailable: false));

        RenderSection().Find("[data-testid='capacity-counts-unavailable']").TextContent.Should()
            .Be("Could not read the running agents from the cluster.");
    }

    [Fact]
    public void LoadFailure_ShowsTheErrorAndNoTable()
    {
        _settings.Setup(s => s.GetAgentCapacityAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));

        var cut = RenderSection();

        cut.Find("[data-testid='capacity-load-error']").TextContent.Should().Be("Could not load the agent capacity.");
        cut.FindAll("[data-testid='capacity-table']").Should().BeEmpty();
    }

    // req 6.3: an empty field is 0; only template fields with a value are sent.
    [Fact]
    public void Save_SendsTheEditedValues_EmptyAsNone()
    {
        var cut = RenderSection();
        cut.Find("input[data-capacity-key='all']").Change("");
        cut.Find("input[data-capacity-key='claude']").Change("");
        cut.Find("input[data-capacity-key='dotnet,kiro']").Change("2");

        cut.Find("[data-testid='capacity-save']").Click();

        _saved.Should().NotBeNull();
        _saved!.MaxConcurrentAgents.Should().Be(0);
        _saved.MaxConcurrentByJobTemplate.Should().Equal(new Dictionary<string, int> { ["dotnet,kiro"] = 2 });
        _statuses.Should().ContainSingle(s => !s.IsError && s.Message == "Saved.");
    }

    // req 6.3: the API's reasons are shown.
    [Fact]
    public void Save_WhenRefused_ShowsTheReasons()
    {
        _settings.Setup(s => s.SaveAgentCapacityAsync(It.IsAny<AgentCapacityLimits>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The limit for all agents must be between 0 and 100 (was 101)."));
        var cut = RenderSection();
        cut.Find("input[data-capacity-key='all']").Change("101");

        cut.Find("[data-testid='capacity-save']").Click();

        cut.Find("[data-testid='capacity-save-error']").TextContent.Should().Be("The limit for all agents must be between 0 and 100 (was 101).");
    }
}
