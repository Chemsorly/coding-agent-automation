using Bunit;
using CodingAgentWebUI.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace CodingAgentWebUI.UnitTests.Components;

public class OnboardingChecklistComponentTests : BunitContext
{
    private readonly Mock<IJSRuntime> _mockJs = new();

    public OnboardingChecklistComponentTests()
    {
        Services.AddSingleton<IJSRuntime>(_mockJs.Object);
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
        Assert.Contains(links, l => l.GetAttribute("href") == "settings?section=providers-issue");
        Assert.Contains(links, l => l.GetAttribute("href") == "settings?section=providers-repository");
        Assert.Contains(links, l => l.GetAttribute("href") == "settings?section=projects");
        Assert.Contains(links, l => l.GetAttribute("href") == "agent-coding");
        // TODO: The template link uses @onclick:preventDefault so the href is never navigated on click.
        // This assertion only validates the href attribute, not that clicking triggers OnAddTemplate.
        // Consider adding a test that verifies the OnAddTemplate callback is invoked on click.
        Assert.Contains(links, l => l.GetAttribute("href") == "agent-monitoring");
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
        _mockJs.Setup(j => j.InvokeAsync<string?>("localStorageGet", It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
            .ReturnsAsync("true");

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
        Assert.DoesNotContain("Getting Started", cut.Markup);
    }

    [Theory]
    [InlineData(0, false, false, false, false, false, false)]
    [InlineData(3, true, true, true, false, false, false)]
    [InlineData(5, true, true, true, true, true, false)]
    public void Checklist_ShowsProgressCount(int expectedCount, bool hasIssue, bool hasRepo, bool hasProject, bool hasTemplate, bool hasAgent, bool isLoop)
    {
        var cut = Render<OnboardingChecklist>(p => p
            .Add(s => s.HasIssueProvider, hasIssue)
            .Add(s => s.HasRepoProvider, hasRepo)
            .Add(s => s.HasProject, hasProject)
            .Add(s => s.HasTemplate, hasTemplate)
            .Add(s => s.HasAgent, hasAgent)
            .Add(s => s.IsLoopActive, isLoop)
            .Add(s => s.OnAddTemplate, EventCallback.Empty));

        Assert.Contains($"{expectedCount} of 6 steps complete", cut.Markup);
    }

    [Theory]
    [InlineData(0, "0", false, false, false, false, false, false)]
    [InlineData(50, "3", true, true, true, false, false, false)]
    // The [InlineData(100, ...)] row has been removed — it always hit the early-return guard
    // and never executed assertions (self-documented TODO in original code).
    public void Checklist_ProgressBarWidth_ReflectsCompletion(int expectedWidth, string expectedAriaValue, bool hasIssue, bool hasRepo, bool hasProject, bool hasTemplate, bool hasAgent, bool isLoop)
    {
        // When all complete, the component hides — skip that case
        if (hasIssue && hasRepo && hasProject && hasTemplate && hasAgent && isLoop)
            return;

        var cut = Render<OnboardingChecklist>(p => p
            .Add(s => s.HasIssueProvider, hasIssue)
            .Add(s => s.HasRepoProvider, hasRepo)
            .Add(s => s.HasProject, hasProject)
            .Add(s => s.HasTemplate, hasTemplate)
            .Add(s => s.HasAgent, hasAgent)
            .Add(s => s.IsLoopActive, isLoop)
            .Add(s => s.OnAddTemplate, EventCallback.Empty));

        var progressFill = cut.Find(".progress-fill");
        Assert.Contains($"width: {expectedWidth}%", progressFill.GetAttribute("style"));

        var progressBar = cut.Find("[role='progressbar']");
        Assert.Equal(expectedAriaValue, progressBar.GetAttribute("aria-valuenow"));
    }

    [Fact]
    public void Checklist_HighlightsCurrentStep()
    {
        // First two steps complete, third should be "current"
        var cut = Render<OnboardingChecklist>(p => p
            .Add(s => s.HasIssueProvider, true)
            .Add(s => s.HasRepoProvider, true)
            .Add(s => s.HasProject, false)
            .Add(s => s.HasTemplate, false)
            .Add(s => s.HasAgent, false)
            .Add(s => s.IsLoopActive, false)
            .Add(s => s.OnAddTemplate, EventCallback.Empty));

        var steps = cut.FindAll(".onboarding-steps li");
        Assert.Equal(6, steps.Count);

        // Steps 1-2: complete
        Assert.Contains("step-complete", steps[0].ClassName);
        Assert.Contains("step-complete", steps[1].ClassName);
        // Step 3: current (first incomplete)
        Assert.Contains("step-current", steps[2].ClassName);
        // Steps 4-6: neither complete nor current
        Assert.DoesNotContain("step-complete", steps[3].ClassName ?? "");
        Assert.DoesNotContain("step-current", steps[3].ClassName ?? "");
    }

    [Fact]
    public void Checklist_CompletedStepsHaveCheckCircleIcon()
    {
        var cut = Render<OnboardingChecklist>(p => p
            .Add(s => s.HasIssueProvider, true)
            .Add(s => s.HasRepoProvider, false)
            .Add(s => s.HasProject, false)
            .Add(s => s.HasTemplate, false)
            .Add(s => s.HasAgent, false)
            .Add(s => s.IsLoopActive, false)
            .Add(s => s.OnAddTemplate, EventCallback.Empty));

        var steps = cut.FindAll(".onboarding-steps li");

        // First step is complete — should have check-circle icon
        var checkIcon = steps[0].QuerySelector("[data-icon='check-circle']");
        Assert.NotNull(checkIcon);
    }

    [Fact]
    public void Checklist_PendingStepsHaveCircleIcon()
    {
        var cut = Render<OnboardingChecklist>(p => p
            .Add(s => s.HasIssueProvider, false)
            .Add(s => s.HasRepoProvider, false)
            .Add(s => s.HasProject, false)
            .Add(s => s.HasTemplate, false)
            .Add(s => s.HasAgent, false)
            .Add(s => s.IsLoopActive, false)
            .Add(s => s.OnAddTemplate, EventCallback.Empty));

        var steps = cut.FindAll(".onboarding-steps li");

        // All steps are pending — should have circle icon
        var circleIcon = steps[0].QuerySelector("[data-icon='circle']");
        Assert.NotNull(circleIcon);
    }

    [Fact]
    public void Checklist_FirstIncompleteStep_IsCurrentWhenNoneComplete()
    {
        var cut = Render<OnboardingChecklist>(p => p
            .Add(s => s.HasIssueProvider, false)
            .Add(s => s.HasRepoProvider, false)
            .Add(s => s.HasProject, false)
            .Add(s => s.HasTemplate, false)
            .Add(s => s.HasAgent, false)
            .Add(s => s.IsLoopActive, false)
            .Add(s => s.OnAddTemplate, EventCallback.Empty));

        var steps = cut.FindAll(".onboarding-steps li");

        // First step should be current
        Assert.Contains("step-current", steps[0].ClassName);
        // Second step should NOT be current
        Assert.DoesNotContain("step-current", steps[1].ClassName ?? "");
    }
}
