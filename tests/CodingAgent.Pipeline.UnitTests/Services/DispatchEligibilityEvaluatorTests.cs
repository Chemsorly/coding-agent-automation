using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="DispatchEligibilityEvaluator"/>.
///
/// Covers the five evaluation methods that centralise the dispatch-eligibility decision
/// across the five dispatch sites (Issue #3345).
///
/// Key behaviors under test:
/// — EvaluateLabelFilter: returns FilteredByLabel when any label matches the filter set
/// — EvaluateActiveElsewhere: returns ActiveElsewhere when either boolean is true
/// — EvaluateDependencyAsync: returns BlockedByDependency when any open dep exists
/// — EvaluateConcurrencyLimit: returns ConcurrencyExhausted when activeCount >= maxAllowed
/// — EvaluateSelectorBlocked: returns SelectorBlocked when selector is in stopped set
///
/// TODO: [WARNING] These tests cover the evaluator in isolation only. The issue prerequisites
/// required characterization tests at each of the five call sites (DispatchScheduler.Issues.cs,
/// DispatchScheduler.Decomposition.cs, DispatchScheduler.cs, OrphanedLabelRecoveryService.cs,
/// WorkItemDispatchLoop.cs, BlockedIssuesService.cs) asserting the correct filter set and
/// evaluation method is invoked at each site. Without those tests, a site could pass the wrong
/// filter set (e.g. the scheduler's narrow Error+NeedsRefinement set instead of BlockedIssuesService's
/// wider NotReadyLabels set) and no test would fail.
/// </summary>
public sealed class DispatchEligibilityEvaluatorTests
{
    private readonly DispatchEligibilityEvaluator _sut = new();

    // ── EvaluateLabelFilter ─────────────────────────────────────────────────

    [Fact]
    public void EvaluateLabelFilter_WhenIssueLabelledError_ReturnsFilteredByLabel()
    {
        var filterSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AgentLabels.Error,
            AgentLabels.NeedsRefinement,
        };

        var result = _sut.EvaluateLabelFilter(new[] { AgentLabels.Error }, filterSet);

        result.Verdict.Should().Be(EligibilityVerdict.FilteredByLabel);
        result.IsEligible.Should().BeFalse();
    }

    [Fact]
    public void EvaluateLabelFilter_WhenIssueLabelledNeedsRefinement_ReturnsFilteredByLabel()
    {
        var filterSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AgentLabels.Error,
            AgentLabels.NeedsRefinement,
        };

        var result = _sut.EvaluateLabelFilter(new[] { AgentLabels.NeedsRefinement }, filterSet);

        result.Verdict.Should().Be(EligibilityVerdict.FilteredByLabel);
        result.IsEligible.Should().BeFalse();
    }

    [Fact]
    public void EvaluateLabelFilter_WhenNoMatchingLabel_ReturnsEligible()
    {
        var filterSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AgentLabels.Error,
            AgentLabels.NeedsRefinement,
        };

        var result = _sut.EvaluateLabelFilter(new[] { "bug", "enhancement" }, filterSet);

        result.Verdict.Should().Be(EligibilityVerdict.Eligible);
        result.IsEligible.Should().BeTrue();
    }

    [Fact]
    public void EvaluateLabelFilter_WhenEmptyLabels_ReturnsEligible()
    {
        var filterSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AgentLabels.Error };

        var result = _sut.EvaluateLabelFilter(Array.Empty<string>(), filterSet);

        result.Verdict.Should().Be(EligibilityVerdict.Eligible);
    }

    [Fact]
    public void EvaluateLabelFilter_WhenNullLabels_ReturnsEligible()
    {
        var filterSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AgentLabels.Error };

        var result = _sut.EvaluateLabelFilter(null!, filterSet);

        result.Verdict.Should().Be(EligibilityVerdict.Eligible);
    }

    // TODO: [WARNING] Missing test: the null-filterSet guard in EvaluateLabelFilter (filterSet is null → Eligible)
    // is untested. The "allow everything on null filter" behaviour is non-obvious and could be changed to throw
    // ArgumentNullException without any test failing. Add a test: _sut.EvaluateLabelFilter(new[]{"label"}, null!)
    // that asserts the current Eligible verdict (or throws, if that's the desired contract).

    [Fact]
    public void EvaluateLabelFilter_IsCaseInsensitive()
    {
        var filterSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agent:error" };

        var result = _sut.EvaluateLabelFilter(new[] { "AGENT:ERROR" }, filterSet);

        result.Verdict.Should().Be(EligibilityVerdict.FilteredByLabel);
    }

    // ── EvaluateLabelFilter — property test ────────────────────────────────

    /// <summary>
    /// Property: if any label in the issue's label set is present in the filter set,
    /// the result is never Eligible.
    /// </summary>
    [Property]
    public Property EvaluateLabelFilter_WhenAnyLabelMatchesFilterSet_ResultIsNeverEligible()
    {
        // Generate a non-empty list of ASCII labels (to avoid FsCheck default string issues)
        var labelGen =
            Gen.Choose(1, 20)
               .SelectMany(n => Gen.ArrayOf(
                   Gen.Choose(97, 122).Select(c => (char)c), n))  // a-z
               .Select(chars => new string(chars));

        var labelsGen = Gen.NonEmptyListOf(labelGen).Select(l => l.ToArray());

        return Prop.ForAll(labelsGen.ToArbitrary(), (string[] labels) =>
        {
            // Build a filter set that contains at least one of the labels
            var filterSet = new HashSet<string>(StringComparer.Ordinal) { labels[0] };
            var result = _sut.EvaluateLabelFilter(labels, filterSet);
            result.Verdict.Should().NotBe(EligibilityVerdict.Eligible,
                $"label '{labels[0]}' is in the filter set so result must not be Eligible");
        });
    }

    // ── EvaluateActiveElsewhere ─────────────────────────────────────────────

    [Fact]
    public void EvaluateActiveElsewhere_WhenIsBeingProcessed_ReturnsActiveElsewhere()
    {
        var result = _sut.EvaluateActiveElsewhere(isBeingProcessed: true, isInActiveSet: false);

        result.Verdict.Should().Be(EligibilityVerdict.ActiveElsewhere);
        result.IsEligible.Should().BeFalse();
    }

    [Fact]
    public void EvaluateActiveElsewhere_WhenOnlyInActiveSet_ReturnsActiveElsewhere()
    {
        var result = _sut.EvaluateActiveElsewhere(isBeingProcessed: false, isInActiveSet: true);

        result.Verdict.Should().Be(EligibilityVerdict.ActiveElsewhere);
        result.IsEligible.Should().BeFalse();
    }

    [Fact]
    public void EvaluateActiveElsewhere_WhenBothTrue_ReturnsActiveElsewhere()
    {
        var result = _sut.EvaluateActiveElsewhere(isBeingProcessed: true, isInActiveSet: true);

        result.Verdict.Should().Be(EligibilityVerdict.ActiveElsewhere);
    }

    [Fact]
    public void EvaluateActiveElsewhere_WhenBothFalse_ReturnsEligible()
    {
        var result = _sut.EvaluateActiveElsewhere(isBeingProcessed: false, isInActiveSet: false);

        result.Verdict.Should().Be(EligibilityVerdict.Eligible);
        result.IsEligible.Should().BeTrue();
    }

    // ── EvaluateDependencyAsync ─────────────────────────────────────────────

    [Fact]
    public async Task EvaluateDependencyAsync_WhenBlockingIssueIsOpen_ReturnsBlockedByDependency()
    {
        // Arrange: issue body references #5, issue #5 is still open
        // TODO: [WARNING] The providerMock.Setup below is dead code — the evaluator delegates entirely
        // to checkerMock.CheckAsync and never calls provider methods directly. Remove this setup or
        // replace providerMock with Mock<IIssueProvider>(MockBehavior.Strict) to document that
        // the provider is a pass-through and prevent false test confidence.
        var providerMock = new Mock<IIssueProvider>();
        providerMock.Setup(p => p.IsIssueClosedAsync(
                It.Is<IssueIdentifier>(id => id.Value == "5"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);  // open

        var checkerMock = new Mock<IDependencyChecker>();
        checkerMock.Setup(c => c.CheckAsync(
                It.IsAny<IssueIdentifier>(),
                It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(),
                It.IsAny<Dictionary<int, bool>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DependencyCheckResult
            {
                IsReady = false,
                BlockedBy = new[] { 5 },
                TotalDependencies = 1,
            });

        // Act
        // TODO: [WARNING] The stateCache identity is not verified — the checker mock uses It.IsAny<Dictionary<int,bool>>(),
        // so the evaluator could silently create its own internal dictionary and discard the provided cache without
        // any test failing. Add a test that pre-populates stateCache, captures the argument via It.Is<>(d => d == cache)
        // or MockBehavior.Strict, and confirms the same instance is forwarded to checker.CheckAsync.
        var result = await _sut.EvaluateDependencyAsync(
            identifier: "42",
            issueBody: "Blocked by #5",
            provider: providerMock.Object,
            stateCache: new Dictionary<int, bool>(),
            checker: checkerMock.Object,
            ct: CancellationToken.None);

        // Assert
        result.Verdict.Should().Be(EligibilityVerdict.BlockedByDependency);
        result.IsEligible.Should().BeFalse();
        result.Reason.Should().Contain("#5");
    }

    [Fact]
    public async Task EvaluateDependencyAsync_WhenNoDependencies_ReturnsEligible()
    {
        var checkerMock = new Mock<IDependencyChecker>();
        checkerMock.Setup(c => c.CheckAsync(
                It.IsAny<IssueIdentifier>(),
                It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(),
                It.IsAny<Dictionary<int, bool>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var result = await _sut.EvaluateDependencyAsync(
            identifier: "10",
            issueBody: "No blockers here",
            provider: Mock.Of<IIssueProvider>(),
            stateCache: new Dictionary<int, bool>(),
            checker: checkerMock.Object,
            ct: CancellationToken.None);

        result.Verdict.Should().Be(EligibilityVerdict.Eligible);
        result.IsEligible.Should().BeTrue();
    }

    [Fact]
    public async Task EvaluateDependencyAsync_WhenAllDependenciesSatisfied_ReturnsEligible()
    {
        var checkerMock = new Mock<IDependencyChecker>();
        checkerMock.Setup(c => c.CheckAsync(
                It.IsAny<IssueIdentifier>(),
                It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(),
                It.IsAny<Dictionary<int, bool>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DependencyCheckResult
            {
                IsReady = true,
                BlockedBy = Array.Empty<int>(),
                TotalDependencies = 1,
            });

        var result = await _sut.EvaluateDependencyAsync(
            identifier: "11",
            issueBody: "Blocked by #3 (closed)",
            provider: Mock.Of<IIssueProvider>(),
            stateCache: new Dictionary<int, bool>(),
            checker: checkerMock.Object,
            ct: CancellationToken.None);

        result.Verdict.Should().Be(EligibilityVerdict.Eligible);
    }

    // ── EvaluateConcurrencyLimit ────────────────────────────────────────────

    [Fact]
    public void EvaluateConcurrencyLimit_WhenActiveCountEqualsMax_ReturnsConcurrencyExhausted()
    {
        // Models the DispatchDecompositionRoundAsync gate:
        // activeDecompositionCount + additionalDecompDispatches >= config.MaxConcurrentDecompositions
        var result = _sut.EvaluateConcurrencyLimit(activeCount: 3, maxAllowed: 3);

        result.Verdict.Should().Be(EligibilityVerdict.ConcurrencyExhausted);
        result.IsEligible.Should().BeFalse();
    }

    [Fact]
    public void EvaluateConcurrencyLimit_WhenActiveCountExceedsMax_ReturnsConcurrencyExhausted()
    {
        var result = _sut.EvaluateConcurrencyLimit(activeCount: 5, maxAllowed: 3);

        result.Verdict.Should().Be(EligibilityVerdict.ConcurrencyExhausted);
        result.IsEligible.Should().BeFalse();
    }

    [Fact]
    public void EvaluateConcurrencyLimit_WhenActiveCountBelowMax_ReturnsEligible()
    {
        var result = _sut.EvaluateConcurrencyLimit(activeCount: 2, maxAllowed: 3);

        result.Verdict.Should().Be(EligibilityVerdict.Eligible);
        result.IsEligible.Should().BeTrue();
    }

    [Fact]
    public void EvaluateConcurrencyLimit_WhenMaxIsZero_ReturnsConcurrencyExhausted()
    {
        // max=0 means no decomposition allowed
        var result = _sut.EvaluateConcurrencyLimit(activeCount: 0, maxAllowed: 0);

        result.Verdict.Should().Be(EligibilityVerdict.ConcurrencyExhausted);
    }

    [Fact]
    public void EvaluateConcurrencyLimit_WhenActiveCountIsZeroAndMaxIsPositive_ReturnsEligible()
    {
        var result = _sut.EvaluateConcurrencyLimit(activeCount: 0, maxAllowed: 1);

        result.Verdict.Should().Be(EligibilityVerdict.Eligible);
    }

    // ── EvaluateSelectorBlocked ─────────────────────────────────────────────

    [Fact]
    public void EvaluateSelectorBlocked_WhenSelectorIsStopped_ReturnsSelectorBlocked()
    {
        var stopped = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kiro-agent-1" };

        var result = _sut.EvaluateSelectorBlocked("kiro-agent-1", stopped);

        result.Verdict.Should().Be(EligibilityVerdict.SelectorBlocked);
        result.IsEligible.Should().BeFalse();
    }

    [Fact]
    public void EvaluateSelectorBlocked_WhenSelectorIsNotStopped_ReturnsEligible()
    {
        var stopped = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kiro-agent-1" };

        var result = _sut.EvaluateSelectorBlocked("kiro-agent-2", stopped);

        result.Verdict.Should().Be(EligibilityVerdict.Eligible);
    }

    [Fact]
    public void EvaluateSelectorBlocked_IsCaseInsensitive()
    {
        var stopped = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Kiro-Agent-1" };

        var result = _sut.EvaluateSelectorBlocked("kiro-agent-1", stopped);

        result.Verdict.Should().Be(EligibilityVerdict.SelectorBlocked);
    }

    // ── DispatchEligibilityResult static factory helpers ───────────────────

    [Fact]
    public void BlockedByDependency_WithBlockedNumbers_IncludesNumbersInReason()
    {
        var result = DispatchEligibilityResult.BlockedByDependency(new[] { 5, 12 });

        result.Verdict.Should().Be(EligibilityVerdict.BlockedByDependency);
        result.Reason.Should().Contain("#5");
        result.Reason.Should().Contain("#12");
    }

    [Fact]
    public void BlockedByDependency_WithEmptyList_ProducesGenericReason()
    {
        var result = DispatchEligibilityResult.BlockedByDependency(Array.Empty<int>());

        result.Verdict.Should().Be(EligibilityVerdict.BlockedByDependency);
        result.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Eligible_ReturnsEligibleVerdict()
    {
        var result = DispatchEligibilityResult.Eligible();

        result.Verdict.Should().Be(EligibilityVerdict.Eligible);
        result.IsEligible.Should().BeTrue();
    }
}
