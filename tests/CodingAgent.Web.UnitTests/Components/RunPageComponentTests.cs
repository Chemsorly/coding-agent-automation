using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for RunPage verifying that BuildRunModelFromSummary correctly propagates
/// RunType from the summary to the PipelineRun view model, so the PipelineSidebar renders
/// the correct phase groups for decomposition runs. See issue #2600.
/// </summary>
public class RunPageComponentTests : BunitContext
{
    // ── Shared scaffolding ────────────────────────────────────────────────

    /// <summary>
    /// Registers all services required by RunPage into bUnit's service collection,
    /// configured so that GetRunAsync returns the provided summary and the page renders
    /// in a completed (non-live) state.
    /// </summary>
    private void RegisterServices(PipelineRunSummary summary)
    {
        var mockHub = new Mock<IAgentHubConnection>();
        mockHub.Setup(h => h.State).Returns(HubConnectionState.Disconnected);
        mockHub.Setup(h => h.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockHub.Setup(h => h.On(It.IsAny<string>(), It.IsAny<Action>())).Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.On<It.IsAnyType>(It.IsAny<string>(), It.IsAny<Action<It.IsAnyType>>()))
            .Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.On<It.IsAnyType, It.IsAnyType>(It.IsAny<string>(), It.IsAny<Action<It.IsAnyType, It.IsAnyType>>()))
            .Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.On<It.IsAnyType, It.IsAnyType, It.IsAnyType>(It.IsAny<string>(), It.IsAny<Action<It.IsAnyType, It.IsAnyType, It.IsAnyType>>()))
            .Returns(Mock.Of<IDisposable>());
        mockHub.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var mockRunHistory = new Mock<IPipelineApiRunHistoryClient>();
        mockRunHistory
            .Setup(c => c.GetRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var mockConfigClient = new Mock<IPipelineApiConfigClient>();
        mockConfigClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var mockWorkItems = new Mock<IPipelineApiWorkItemClient>();

        Services.AddSingleton(mockHub.Object);
        Services.AddSingleton(mockRunHistory.Object);
        Services.AddSingleton(mockWorkItems.Object);
        Services.AddSingleton(mockConfigClient.Object);
        Services.AddSingleton(Mock.Of<IConfigurationStore>());
        Services.AddSingleton(new CockpitState());
    }

    /// <summary>
    /// Builds a minimal completed PipelineRunSummary with the specified RunType and FinalStep.
    /// </summary>
    private static PipelineRunSummary MakeSummary(PipelineRunType runType, PipelineStep finalStep)
    {
        var runId = Guid.NewGuid().ToString();
        // TODO [WARNING]: BrainRepoUsed is left false (default), so BrainProviderConfigId will be null on the
        // resulting model, causing SyncingBrainRepoPreRun and SyncingBrainRepoPostRun steps to be hidden by
        // the brain-provider guard. This differs silently from a typical real decomp run. Consider adding a
        // MakeSummary overload or a separate test that exercises BrainRepoUsed = true.
        return new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = "2600",
            IssueTitle = "Decomp test issue",
            FinalStep = finalStep,
            RunType = runType,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            // TODO [WARNING]: DecompositionSubIssuesAttempted is left at 0. The acceptance criterion
            // "The sub-issues stat row renders when DecompositionSubIssuesAttempted > 0 on a decomp run"
            // is not covered. Add a test that sets DecompositionSubIssuesAttempted > 0 and asserts the
            // stat row is visible, and another that confirms it is absent when the value is 0.
        };
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    /// <summary>
    /// When GetRunAsync returns a summary with RunType = DecompositionAnalysis,
    /// BuildRunModelFromSummary must propagate that RunType to the PipelineRun view model
    /// so PipelineSidebar renders the "Decomposition Analysis" phase group and hides the
    /// implementation-only phase groups.
    /// </summary>
    [Fact]
    public void BuildRunModelFromSummary_WithDecompositionAnalysisSummary_SetsRunTypeOnModel()
    {
        var summary = MakeSummary(PipelineRunType.DecompositionAnalysis, PipelineStep.PostingPlan);
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        // Positive: decomposition-analysis phase must be visible
        // TODO [WARNING]: The positive assertion verifies sidebar rendering but does not directly assert
        // model.RunType == PipelineRunType.DecompositionAnalysis. If IsHiddenForDecompositionRun is later
        // changed, tests could continue to pass even if RunType propagation were broken. Consider also
        // asserting on the run-type badge element (AC-3: "Decomp" badge) for stronger coverage of the
        // stated acceptance criterion. The Preparation phase (Created, CloningRepository, etc.) is not
        // checked for absence — it correctly remains visible for decomp runs, but is not explicitly
        // asserted here.
        Assert.NotNull(cut.Find("[data-testid='phase-decomposition-analysis']"));

        // Negative: implementation-only phases must be hidden by IsHiddenForDecompositionRun
        Assert.Empty(cut.FindAll("[data-testid='phase-analysis']"));
        Assert.Empty(cut.FindAll("[data-testid='phase-code-generation']"));
    }

    /// <summary>
    /// When GetRunAsync returns a summary with RunType = Decomposition,
    /// BuildRunModelFromSummary must propagate that RunType to the PipelineRun view model
    /// so PipelineSidebar renders the "Decomposition" phase group and hides the
    /// implementation-only phase groups.
    /// </summary>
    [Fact]
    public void BuildRunModelFromSummary_WithDecompositionSummary_SetsRunTypeOnModel()
    {
        var summary = MakeSummary(PipelineRunType.Decomposition, PipelineStep.PostingSummary);
        RegisterServices(summary);

        var cut = Render<RunPage>(ps => ps.Add(p => p.RunId, summary.RunId));

        // Positive: decomposition phase must be visible
        // TODO [WARNING]: Same as above — consider also asserting on the run-type badge (AC-3) and the
        // broader phase visibility (e.g., Preparation phase remains visible for decomp runs, which is
        // correct, but is not explicitly guarded here).
        Assert.NotNull(cut.Find("[data-testid='phase-decomposition']"));

        // Negative: implementation-only phases must be hidden by IsHiddenForDecompositionRun
        Assert.Empty(cut.FindAll("[data-testid='phase-analysis']"));
        Assert.Empty(cut.FindAll("[data-testid='phase-code-generation']"));
    }
}
