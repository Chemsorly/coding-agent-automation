using Bunit;
using CodingAgentWebUI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace CodingAgentWebUI.UnitTests.Components;

public class OnboardingChecklistComponentTests : BunitContext
{
    public OnboardingChecklistComponentTests()
    {
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
    }

    [Fact]
    public void Checklist_RendersWhenIncomplete()
    {
        var cut = Render<OnboardingChecklist>(p => p
            .Add(s => s.HasIssueProvider, false)
            .Add(s => s.HasRepoProvider, false)
            .Add(s => s.HasProject, false)
            .Add(s => s.HasTemplate, false)
            .Add(s => s.HasAgent, false)
            .Add(s => s.IsLoopActive, false)
            .Add(s => s.OnAddTemplate, EventCallback.Empty));

        Assert.Contains("Getting Started", cut.Markup);
        Assert.Contains("Create an Issue Provider", cut.Markup);
        Assert.Contains("Create a Repository Provider", cut.Markup);
        Assert.Contains("Create a Project", cut.Markup);
        Assert.Contains("Create a Pipeline Template", cut.Markup);
        Assert.Contains("Register an Agent", cut.Markup);
        Assert.Contains("Start the pipeline loop", cut.Markup);
    }

    [Fact]
    public void Checklist_HidesWhenAllComplete()
    {
        var cut = Render<OnboardingChecklist>(p => p
            .Add(s => s.HasIssueProvider, true)
            .Add(s => s.HasRepoProvider, true)
            .Add(s => s.HasProject, true)
            .Add(s => s.HasTemplate, true)
            .Add(s => s.HasAgent, true)
            .Add(s => s.IsLoopActive, true)
            .Add(s => s.OnAddTemplate, EventCallback.Empty));

        Assert.DoesNotContain("Getting Started", cut.Markup);
    }

    [Fact]
    public void Checklist_ShowsCheckmarkForCompletedSteps()
    {
        var cut = Render<OnboardingChecklist>(p => p
            .Add(s => s.HasIssueProvider, true)
            .Add(s => s.HasRepoProvider, false)
            .Add(s => s.HasProject, true)
            .Add(s => s.HasTemplate, false)
            .Add(s => s.HasAgent, false)
            .Add(s => s.IsLoopActive, false)
            .Add(s => s.OnAddTemplate, EventCallback.Empty));

        var steps = cut.FindAll(".onboarding-steps li");
        Assert.Equal(6, steps.Count);

        // Step 1 (Issue Provider) — complete
        Assert.Contains("step-complete", steps[0].ClassName);
        // Step 2 (Repo Provider) — incomplete
        Assert.DoesNotContain("step-complete", steps[1].ClassName ?? "");
        // Step 3 (Project) — complete
        Assert.Contains("step-complete", steps[2].ClassName);
    }

    [Fact]
    public void Checklist_LinksPointToCorrectTargets()
    {
        var cut = Render<OnboardingChecklist>(p => p
            .Add(s => s.HasIssueProvider, false)
            .Add(s => s.HasRepoProvider, false)
            .Add(s => s.HasProject, false)
            .Add(s => s.HasTemplate, false)
            .Add(s => s.HasAgent, false)
            .Add(s => s.IsLoopActive, false)
            .Add(s => s.OnAddTemplate, EventCallback.Empty));

        var links = cut.FindAll("a[href]");
        Assert.Contains(links, l => l.GetAttribute("href") == "/settings?section=providers-issue");
        Assert.Contains(links, l => l.GetAttribute("href") == "/settings?section=providers-repository");
        Assert.Contains(links, l => l.GetAttribute("href") == "/settings?section=projects");
        Assert.Contains(links, l => l.GetAttribute("href") == "/agent-monitoring");
    }

    [Fact]
    public void Checklist_DismissButtonHidesChecklist()
    {
        var cut = Render<OnboardingChecklist>(p => p
            .Add(s => s.HasIssueProvider, false)
            .Add(s => s.HasRepoProvider, false)
            .Add(s => s.HasProject, false)
            .Add(s => s.HasTemplate, false)
            .Add(s => s.HasAgent, false)
            .Add(s => s.IsLoopActive, false)
            .Add(s => s.OnAddTemplate, EventCallback.Empty));

        Assert.Contains("Getting Started", cut.Markup);

        cut.Find(".onboarding-dismiss").Click();

        Assert.DoesNotContain("Getting Started", cut.Markup);
    }

    [Fact]
    public void Checklist_HiddenWhenDismissedViaLocalStorage()
    {
        // TODO: This registers a second IJSRuntime after the constructor already registered one. DI resolution order may cause the mock setup to be ignored. Consider removing the constructor registration or using a different test setup pattern.
        var mockJs = new Mock<IJSRuntime>();
        mockJs.Setup(j => j.InvokeAsync<string?>("localStorageGet", It.IsAny<object[]>()))
            .ReturnsAsync("true");
        Services.AddSingleton(mockJs.Object);

        var cut = Render<OnboardingChecklist>(p => p
            .Add(s => s.HasIssueProvider, false)
            .Add(s => s.HasRepoProvider, false)
            .Add(s => s.HasProject, false)
            .Add(s => s.HasTemplate, false)
            .Add(s => s.HasAgent, false)
            .Add(s => s.IsLoopActive, false)
            .Add(s => s.OnAddTemplate, EventCallback.Empty));

        // After OnAfterRenderAsync runs, component should be hidden
        cut.WaitForState(() => !cut.Markup.Contains("Getting Started"), TimeSpan.FromSeconds(1));
    }
}
