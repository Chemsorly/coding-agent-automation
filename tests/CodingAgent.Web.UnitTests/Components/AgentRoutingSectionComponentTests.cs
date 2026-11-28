using AwesomeAssertions;
using Bunit;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Ui;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Spec 053 req 2.1–2.8: the Routing page edits the global routing table, says that a change applies to work created
/// after the save, shows the API's refusal line by line, and holds Fetch Models and its timeout (moved from the former
/// agent provider page).
/// </summary>
public class AgentRoutingSectionComponentTests : BunitContext
{
    private static readonly UiRoutingProfile Kiro = new("p-kiro", "Kiro dotnet", JobTemplateProviderType.Kiro, "dotnet,kiro");

    private readonly Mock<IUiSettingsClient> _settings = new();
    private readonly Mock<IUiProviderClient> _providers = new();
    private readonly List<(string Message, bool IsError)> _statuses = [];
    private AgentRouting? _saved;
    private PipelineConfiguration _config = new() { ModelFetchTimeoutSeconds = 120 };

    public AgentRoutingSectionComponentTests()
    {
        _settings.Setup(s => s.GetAgentRoutingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UiAgentRoutingPage(new AgentRouting { Default = new AgentRoute(Kiro.Id, "auto", "auto") }, [Kiro], []));
        _settings.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _config);
        _settings.Setup(s => s.SaveAgentRoutingAsync(It.IsAny<AgentRouting>(), It.IsAny<CancellationToken>()))
            .Callback<AgentRouting, CancellationToken>((r, _) => _saved = r)
            .Returns(Task.CompletedTask);
        _settings.Setup(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Callback<Func<PipelineConfiguration, PipelineConfiguration>, CancellationToken>((transform, _) => _config = transform(_config))
            .Returns(Task.CompletedTask);
    }

    private IRenderedComponent<AgentRoutingSection> RenderSection() =>
        Render<AgentRoutingSection>(p => p
            .Add(s => s.SettingsClient, _settings.Object)
            .Add(s => s.ProviderClient, _providers.Object)
            .Add(s => s.OnShowStatus, (ValueTuple<string, bool> s) => _statuses.Add(s)));

    [Fact]
    public void ThePage_SaysThatAChangeAppliesToQueuedWorkAtItsNextDispatch_Spec056()
    {
        var cut = RenderSection();

        cut.Find("[data-testid='routing-applies-note']").TextContent.Should().Contain("Changes apply to queued work at its next dispatch");
        cut.FindAll("[data-testid='route-table'] tbody tr").Should().HaveCount(6);
    }

    [Fact]
    public void Save_SendsTheEditedTable()
    {
        var cut = RenderSection();
        cut.Find("[data-testid='route-set-triage']").Click();
        cut.Find("[data-testid='route-model-triage']").Change("claude-sonnet-4");

        cut.Find("[data-testid='routing-save']").Click();

        _saved.Should().BeEquivalentTo(new AgentRouting
        {
            Default = new AgentRoute(Kiro.Id, "auto", "auto"),
            ByTaskType = new Dictionary<WorkItemTaskType, AgentRoute> { [WorkItemTaskType.Triage] = new(Kiro.Id, "claude-sonnet-4", "auto") },
        });
        _statuses.Should().ContainSingle(s => !s.IsError);
        _settings.Verify(s => s.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()),
            Times.Never, "the timeout did not change");
    }

    [Fact]
    public void ARefusedSave_ShowsTheApisLines()
    {
        _settings.Setup(s => s.SaveAgentRoutingAsync(It.IsAny<AgentRouting>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Decomposition: model is required (auto lets the tool choose)\nTriage: OpenCode has no effort setting; use auto"));
        var cut = RenderSection();

        cut.Find("[data-testid='routing-save']").Click();

        cut.Find("[data-testid='routing-save-error']").TextContent.Should()
            .Be("Decomposition: model is required (auto lets the tool choose)\nTriage: OpenCode has no effort setting; use auto");
    }

    private const string SectionLandmarks =
        "[data-testid='route-light-hint'], [data-testid='route-table'], [data-testid='routing-save-error'], [data-testid='routing-save'], "
        + "[data-testid='kiro-models-section'], [data-testid='routing-fetch-models'], [data-setting='ModelFetchTimeoutSeconds'], "
        + "[data-testid='kiro-models-save']";

    // The landmarks of the section in document order; querying one selector list returns them in that order.
    private static IReadOnlyList<string> LandmarkNames(IRenderedComponent<AgentRoutingSection> cut) =>
        cut.FindAll(SectionLandmarks)
            .Select(e => e.GetAttribute("data-testid") ?? e.GetAttribute("data-setting") ?? e.LocalName)
            .ToList();

    [Fact]
    public void TheLightHint_LeadsTheTable_AndKiroModelsIsACardOfItsOwnBelowTheRoutingSave()
    {
        var cut = RenderSection();

        LandmarkNames(cut).Should().Equal(
            "route-light-hint", "route-table", "routing-save",
            "kiro-models-section", "routing-fetch-models", "ModelFetchTimeoutSeconds", "kiro-models-save");
        cut.Find("[data-testid='kiro-models-section']").ParentElement!.GetAttribute("data-testid")
            .Should().NotBe("agent-routing-section", "Kiro models is its own card, not part of the routing card");
    }

    [Fact]
    public void ARefusedSave_ShowsItsErrorBetweenTheTableAndSave()
    {
        _settings.Setup(s => s.SaveAgentRoutingAsync(It.IsAny<AgentRouting>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Triage: OpenCode has no effort setting; use auto"));
        var cut = RenderSection();

        cut.Find("[data-testid='routing-save']").Click();

        LandmarkNames(cut).Take(4).Should().Equal("route-light-hint", "route-table", "routing-save-error", "routing-save");
    }

    [Fact]
    public void TheFetchTimeout_IsSavedByTheKiroModelsSave_NotByTheRoutingSave()
    {
        var cut = RenderSection();
        cut.Find("[data-setting='ModelFetchTimeoutSeconds'] input").Change(300);

        cut.Find("[data-testid='routing-save']").Click();
        _config.ModelFetchTimeoutSeconds.Should().Be(120, "the routing Save saves the routing only");

        cut.Find("[data-testid='kiro-models-save']").Click();
        _config.ModelFetchTimeoutSeconds.Should().Be(300);
        cut.Find("[data-testid='kiro-models-save']").HasAttribute("disabled").Should().BeTrue("nothing is left to save");
    }

    [Fact]
    public void FetchModels_OffersTheKiroModelsOnKiroRows()
    {
        _providers.Setup(p => p.GetModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<AgentModelInfo>)[new() { ModelId = "auto" }, new() { ModelId = "claude-sonnet-4" }], (string?)null));
        var cut = RenderSection();

        cut.Find("[data-testid='routing-fetch-models']").Click();

        cut.WaitForAssertion(() => cut.FindAll("#route-models-default option").Select(o => o.GetAttribute("value"))
            .Should().Equal("auto", "claude-sonnet-4"));
        _statuses.Should().Contain(("1 model(s) loaded.", false));
    }
}
