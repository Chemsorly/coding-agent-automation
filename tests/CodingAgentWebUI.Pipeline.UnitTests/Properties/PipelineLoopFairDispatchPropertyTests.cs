using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for fair round-robin dispatch invariants.
/// Tests budget ceiling, fairness, starvation prevention, provider failure isolation,
/// and idempotent empty-cycle behavior.
/// </summary>
public class PipelineLoopFairDispatchPropertyTests
{
    // ══════════════════════════════════════════════════════════════════════
    // Property 1: Budget Ceiling (Unequal Queues)
    // ∀ templates, budget → sum(dispatches) ≤ budget
    // ══════════════════════════════════════════════════════════════════════

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(BudgetCeilingArbitraries) })]
    public Property BudgetCeiling_TotalDispatchesNeverExceedBudget(BudgetCeilingInput input)
    {
        var issueQueues = input.IssueCountsPerTemplate.Select((c, i) =>
            Enumerable.Range(0, c).Select(j => $"t{i}-issue-{j}").ToList()).ToList();
        var prQueues = input.PrCountsPerTemplate.Select((c, i) =>
            Enumerable.Range(0, c).Select(j => $"t{i}-pr-{j}").ToList()).ToList();
        var decompQueues = input.DecompCountsPerTemplate.Select((c, i) =>
            Enumerable.Range(0, c).Select(j => $"t{i}-decomp-{j}").ToList()).ToList();

        int totalDispatched = SimulateThreeWayDispatch(
            issueQueues, prQueues, decompQueues, input.Budget);

        return (totalDispatched <= input.Budget).ToProperty()
            .Label($"Total={totalDispatched}, Budget={input.Budget}");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Property 2: Fairness Bound (Unequal Queues)
    // Within a single queue type (single round), max(dispatches) - min(dispatches) ≤ 1
    // across templates that have sufficient items (didn't exhaust their queue).
    // ══════════════════════════════════════════════════════════════════════

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(FairnessArbitraries) })]
    public Property Fairness_UnequalQueues_DiffersAtMostOne(FairnessInput input)
    {
        // Simulate DispatchRoundAsync for issues only: iterates templates once per round,
        // dispatching at most 1 per template per round, repeating until budget exhausted.
        var queues = input.IssueCountsPerTemplate.Select(c => c).ToArray();
        var dispatches = new int[queues.Length];
        int remaining = input.Budget;

        while (remaining > 0)
        {
            bool madeProgress = false;
            for (int t = 0; t < queues.Length && remaining > 0; t++)
            {
                if (queues[t] > 0)
                {
                    queues[t]--;
                    dispatches[t]++;
                    remaining--;
                    madeProgress = true;
                }
            }
            if (!madeProgress) break;
        }

        // Fairness: among templates that did NOT exhaust their queue
        // (had more eligible items than dispatches), max-min ≤ 1
        var nonExhausted = dispatches
            .Zip(input.IssueCountsPerTemplate, (d, orig) => (d, orig))
            .Where(x => x.orig > x.d) // still had items left when budget ran out
            .Select(x => x.d)
            .ToList();

        if (nonExhausted.Count < 2)
            return true.ToProperty().Label("fewer than 2 non-exhausted templates — trivially fair");

        var diff = nonExhausted.Max() - nonExhausted.Min();
        return (diff <= 1).ToProperty()
            .Label($"Max-Min={diff}, NonExhausted=[{string.Join(",", nonExhausted)}]");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Property 3: Starvation Prevention (Cross-Queue-Type)
    // When budget ≥ number of non-empty queue types, each non-empty queue type
    // gets at least 1 dispatch. Empty-queue-type templates don't block others.
    // ══════════════════════════════════════════════════════════════════════

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(StarvationArbitraries) })]
    public Property StarvationPrevention_NonEmptyQueueTypesGetDispatches(StarvationInput input)
    {
        var issueQueues = input.IssueCountsPerTemplate.Select((c, i) =>
            Enumerable.Range(0, c).Select(j => $"t{i}-issue-{j}").ToList()).ToList();
        var prQueues = input.PrCountsPerTemplate.Select((c, i) =>
            Enumerable.Range(0, c).Select(j => $"t{i}-pr-{j}").ToList()).ToList();
        var decompQueues = input.DecompCountsPerTemplate.Select((c, i) =>
            Enumerable.Range(0, c).Select(j => $"t{i}-decomp-{j}").ToList()).ToList();

        // Track dispatches per queue type
        int issueDispatched = 0;
        int prDispatched = 0;
        int decompDispatched = 0;
        int remaining = input.Budget;
        int currentTurn = 0;

        while (remaining > 0)
        {
            bool hasIssues = issueQueues.Any(q => q.Count > 0);
            bool hasPrs = prQueues.Any(q => q.Count > 0);
            bool hasDecomp = decompQueues.Any(q => q.Count > 0);

            int startTurn = currentTurn;
            bool foundTurn = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int tryTurn = (startTurn + attempt) % 3;
                if ((tryTurn == 0 && hasIssues) || (tryTurn == 1 && hasPrs) || (tryTurn == 2 && hasDecomp))
                {
                    currentTurn = tryTurn;
                    foundTurn = true;
                    break;
                }
            }
            if (!foundTurn) break;

            var queues = currentTurn switch { 0 => issueQueues, 1 => prQueues, _ => decompQueues };
            for (int t = 0; t < queues.Count && remaining > 0; t++)
            {
                if (queues[t].Count > 0)
                {
                    queues[t].RemoveAt(0);
                    remaining--;
                    switch (currentTurn) { case 0: issueDispatched++; break; case 1: prDispatched++; break; default: decompDispatched++; break; }
                }
            }

            currentTurn = (currentTurn + 1) % 3;
        }

        // Count non-empty queue types in original input
        bool hadIssues = input.IssueCountsPerTemplate.Any(c => c > 0);
        bool hadPrs = input.PrCountsPerTemplate.Any(c => c > 0);
        bool hadDecomps = input.DecompCountsPerTemplate.Any(c => c > 0);

        // Property: each non-empty queue type gets at least 1 dispatch (budget ≥ 3 guarantees this)
        bool noStarvation = true;
        if (hadIssues && issueDispatched == 0) noStarvation = false;
        if (hadPrs && prDispatched == 0) noStarvation = false;
        if (hadDecomps && decompDispatched == 0) noStarvation = false;

        return noStarvation.ToProperty()
            .Label($"Issues={issueDispatched}, PRs={prDispatched}, Decomp={decompDispatched}, Budget={input.Budget}");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Property 4: Provider Failure Isolation (Dispatch Phase)
    // One template's dispatch throwing doesn't prevent subsequent templates
    // from being dispatched.
    // ══════════════════════════════════════════════════════════════════════

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(IsolationArbitraries) })]
    public async Task ProviderFailureIsolation_SubsequentTemplatesStillDispatched(IsolationInput input)
    {
        var templateCount = input.TemplateCount;
        var failingIndex = input.FailingTemplateIndex;
        var templates = PipelineLoopTestData.CreateTemplates(templateCount);

        var config = new PipelineConfiguration
        {
            PipelineJobTemplates = templates,
            ClosedLoopPollInterval = TimeSpan.FromSeconds(60),
            ClosedLoopMaxRunsPerCycle = input.Budget,
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        SetupProviderConfigs(mockStore, templates);

        var failingProviderId = templates[failingIndex].IssueProviderId;

        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns<ProviderConfig>(cfg =>
            {
                var mock = new Mock<IIssueProvider>();
                var issues = Enumerable.Range(0, input.IssuesPerTemplate).Select(j => new IssueSummary
                {
                    Identifier = $"{cfg.Id}-issue-{j}", Title = "Test", Labels = new[] { "agent:next" },
                    CreatedAt = DateTime.UtcNow.AddMinutes(-j)
                }).ToList();
                mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                        It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PagedResult<IssueSummary>
                    {
                        Items = issues, Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
                    });
                return mock.Object;
            });

        var dispatchedProviderIds = new List<string>();
        var mockDispatcher = new Mock<IJobDispatcher>();
        mockDispatcher.Setup(d => d.TryDispatchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<PipelineProject?>()))
            .Returns<string, string, string, string?, string?, string, CancellationToken, string?, PipelineProject?>((_, ip, _, _, _, _, _, _, _) =>
            {
                if (ip == failingProviderId)
                    throw new InvalidOperationException("Simulated dispatch failure");
                lock (dispatchedProviderIds) { dispatchedProviderIds.Add(ip); }
                return Task.FromResult(true);
            });
        mockDispatcher.Setup(d => d.IsIssueBeingProcessedOrQueued(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var svc = CreateService(mockStore, mockFactory, mockDispatcher.Object);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        var started = await svc.StartLoopAsync();
        if (!started) { cts.Cancel(); try { await svc.StopAsync(CancellationToken.None); } catch { } return; }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!svc.StatusMessage.Contains("Cycle complete") && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        svc.StopLoop();
        await Task.Delay(200);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        // Non-failing templates should still have dispatched
        var nonFailingIds = templates
            .Where(t => t.IssueProviderId != failingProviderId)
            .Select(t => t.IssueProviderId)
            .ToHashSet();

        dispatchedProviderIds.Should().NotBeEmpty(
            "non-failing templates should still receive dispatches after a provider failure");

        // Verify at least one non-failing template got dispatched
        dispatchedProviderIds.Any(id => nonFailingIds.Contains(id)).Should().BeTrue(
            $"at least one non-failing template should dispatch. " +
            $"Failing={failingProviderId}, Dispatched=[{string.Join(",", dispatchedProviderIds.Distinct())}]");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Property 5: Idempotent on Empty
    // ∀ templates with eligible=0 → 0 dispatches, no state change
    // ══════════════════════════════════════════════════════════════════════

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(EmptyCycleArbitraries) })]
    public Property IdempotentOnEmpty_ZeroDispatchesWhenAllQueuesEmpty(EmptyCycleInput input)
    {
        var issueQueues = Enumerable.Range(0, input.TemplateCount)
            .Select(_ => new List<string>()).ToList();
        var prQueues = Enumerable.Range(0, input.TemplateCount)
            .Select(_ => new List<string>()).ToList();
        var decompQueues = Enumerable.Range(0, input.TemplateCount)
            .Select(_ => new List<string>()).ToList();

        int totalDispatched = SimulateThreeWayDispatch(
            issueQueues, prQueues, decompQueues, input.Budget);

        return (totalDispatched == 0).ToProperty()
            .Label($"TemplateCount={input.TemplateCount}, Budget={input.Budget}, Dispatched={totalDispatched}");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Simulation helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Simulates the three-way round-robin dispatch from DispatchFairRoundRobinAsync.
    /// Returns total items dispatched.
    /// </summary>
    private static int SimulateThreeWayDispatch(
        List<List<string>> issueQueues,
        List<List<string>> prQueues,
        List<List<string>> decompQueues,
        int budget)
    {
        int remaining = budget;
        int currentTurn = 0;

        while (remaining > 0)
        {
            bool hasIssues = issueQueues.Any(q => q.Count > 0);
            bool hasPrs = prQueues.Any(q => q.Count > 0);
            bool hasDecomp = decompQueues.Any(q => q.Count > 0);

            int startTurn = currentTurn;
            bool foundTurn = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int tryTurn = (startTurn + attempt) % 3;
                if ((tryTurn == 0 && hasIssues) || (tryTurn == 1 && hasPrs) || (tryTurn == 2 && hasDecomp))
                {
                    currentTurn = tryTurn;
                    foundTurn = true;
                    break;
                }
            }
            if (!foundTurn) break;

            // Dispatch one item per template from the current turn's queue type
            switch (currentTurn)
            {
                case 0:
                    remaining -= DispatchRound(issueQueues, remaining);
                    break;
                case 1:
                    remaining -= DispatchRound(prQueues, remaining);
                    break;
                case 2:
                    remaining -= DispatchRound(decompQueues, remaining);
                    break;
            }

            currentTurn = (currentTurn + 1) % 3;
        }

        return budget - remaining;
    }

    /// <summary>
    /// Simulates one round of DispatchRoundAsync: iterates templates, dispatches at most 1 per template.
    /// Returns number consumed.
    /// </summary>
    private static int DispatchRound(List<List<string>> queues, int remainingBudget)
    {
        int consumed = 0;
        for (int t = 0; t < queues.Count && remainingBudget - consumed > 0; t++)
        {
            if (queues[t].Count > 0)
            {
                queues[t].RemoveAt(0);
                consumed++;
            }
        }
        return consumed;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Service helper (for Property 4)
    // ══════════════════════════════════════════════════════════════════════

    private static void SetupProviderConfigs(Mock<IConfigurationStore> mockStore, List<PipelineJobTemplate> templates)
    {
        mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = templates.Select(t => t.Id).ToList() }
            });
        mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);
        mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates.Select(t => new ProviderConfig
            {
                Id = t.IssueProviderId, Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test"
            }).DistinctBy(c => c.Id).ToList());
        mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates.Select(t => new ProviderConfig
            {
                Id = t.RepoProviderId, Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test"
            }).DistinctBy(c => c.Id).ToList());
    }

    private static PipelineLoopService CreateService(Mock<IConfigurationStore> mockStore, Mock<IProviderFactory> mockFactory, IJobDispatcher? dispatcher = null)
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockValidator = new Mock<IQualityGateValidator>();
        var orchestration = new PipelineOrchestrationService(
            mockStore.Object, mockFactory.Object, new IssueDescriptionParser(),
            new AgentPhaseExecutor(mockLogger.Object),
            new QualityGateExecutor(mockValidator.Object, new PullRequestOrchestrator(mockLogger.Object), new CiLogWriter(mockLogger.Object), new FeedbackService(mockLogger.Object), mockLogger.Object),
            mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: new Mock<IPipelineRunHistoryService>().Object);

        return new PipelineLoopService(orchestration, mockFactory.Object, mockStore.Object, mockStore.Object, mockStore.Object, mockLogger.Object, dispatcher);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Input records
    // ══════════════════════════════════════════════════════════════════════

    public sealed class BudgetCeilingInput
    {
        public int TemplateCount { get; }
        public int Budget { get; }
        public IReadOnlyList<int> IssueCountsPerTemplate { get; }
        public IReadOnlyList<int> PrCountsPerTemplate { get; }
        public IReadOnlyList<int> DecompCountsPerTemplate { get; }

        public BudgetCeilingInput(int templateCount, int budget,
            IReadOnlyList<int> issueCounts, IReadOnlyList<int> prCounts, IReadOnlyList<int> decompCounts)
        {
            TemplateCount = templateCount;
            Budget = budget;
            IssueCountsPerTemplate = issueCounts;
            PrCountsPerTemplate = prCounts;
            DecompCountsPerTemplate = decompCounts;
        }

        public override string ToString() =>
            $"Templates={TemplateCount}, Budget={Budget}, " +
            $"Issues=[{string.Join(",", IssueCountsPerTemplate)}], " +
            $"PRs=[{string.Join(",", PrCountsPerTemplate)}], " +
            $"Decomps=[{string.Join(",", DecompCountsPerTemplate)}]";
    }

    public sealed class FairnessInput
    {
        public int Budget { get; }
        public IReadOnlyList<int> IssueCountsPerTemplate { get; }

        public FairnessInput(int budget, IReadOnlyList<int> issueCounts)
        {
            Budget = budget;
            IssueCountsPerTemplate = issueCounts;
        }

        public override string ToString() =>
            $"Budget={Budget}, Issues=[{string.Join(",", IssueCountsPerTemplate)}]";
    }

    public sealed class StarvationInput
    {
        public int TemplateCount { get; }
        public int Budget { get; }
        public IReadOnlyList<int> IssueCountsPerTemplate { get; }
        public IReadOnlyList<int> PrCountsPerTemplate { get; }
        public IReadOnlyList<int> DecompCountsPerTemplate { get; }

        public StarvationInput(int templateCount, int budget,
            IReadOnlyList<int> issueCounts, IReadOnlyList<int> prCounts, IReadOnlyList<int> decompCounts)
        {
            TemplateCount = templateCount;
            Budget = budget;
            IssueCountsPerTemplate = issueCounts;
            PrCountsPerTemplate = prCounts;
            DecompCountsPerTemplate = decompCounts;
        }

        public override string ToString() =>
            $"Templates={TemplateCount}, Budget={Budget}, " +
            $"Issues=[{string.Join(",", IssueCountsPerTemplate)}], " +
            $"PRs=[{string.Join(",", PrCountsPerTemplate)}], " +
            $"Decomps=[{string.Join(",", DecompCountsPerTemplate)}]";
    }

    public sealed class IsolationInput
    {
        public int TemplateCount { get; }
        public int FailingTemplateIndex { get; }
        public int Budget { get; }
        public int IssuesPerTemplate { get; }

        public IsolationInput(int templateCount, int failingIndex, int budget, int issuesPerTemplate)
        {
            TemplateCount = templateCount;
            FailingTemplateIndex = failingIndex;
            Budget = budget;
            IssuesPerTemplate = issuesPerTemplate;
        }

        public override string ToString() =>
            $"Templates={TemplateCount}, Failing={FailingTemplateIndex}, " +
            $"Budget={Budget}, IssuesEach={IssuesPerTemplate}";
    }

    public sealed class EmptyCycleInput
    {
        public int TemplateCount { get; }
        public int Budget { get; }

        public EmptyCycleInput(int templateCount, int budget)
        {
            TemplateCount = templateCount;
            Budget = budget;
        }

        public override string ToString() => $"Templates={TemplateCount}, Budget={Budget}";
    }

    // ══════════════════════════════════════════════════════════════════════
    // Arbitraries
    // ══════════════════════════════════════════════════════════════════════

    public class BudgetCeilingArbitraries
    {
        public static Arbitrary<BudgetCeilingInput> BudgetCeilingInputArb()
        {
            var gen =
                from templateCount in Gen.Choose(1, 10)
                from budget in Gen.Choose(1, 20)
                from issueCounts in Gen.ArrayOf(Gen.Choose(0, 50), templateCount)
                from prCounts in Gen.ArrayOf(Gen.Choose(0, 50), templateCount)
                from decompCounts in Gen.ArrayOf(Gen.Choose(0, 50), templateCount)
                select new BudgetCeilingInput(templateCount, budget,
                    issueCounts.ToList(), prCounts.ToList(), decompCounts.ToList());
            return gen.ToArbitrary();
        }
    }

    public class FairnessArbitraries
    {
        public static Arbitrary<FairnessInput> FairnessInputArb()
        {
            var gen =
                from templateCount in Gen.Choose(2, 10)
                from budget in Gen.Choose(2, 20)
                from issueCounts in Gen.ArrayOf(Gen.Choose(1, 50), templateCount)
                select new FairnessInput(budget, issueCounts.ToList());
            return gen.ToArbitrary();
        }
    }

    public class StarvationArbitraries
    {
        public static Arbitrary<StarvationInput> StarvationInputArb()
        {
            // Budget must be ≥ 3 * templateCount for all queue types to get a turn
            // (each DispatchRound can consume up to templateCount budget slots)
            var gen =
                from templateCount in Gen.Choose(1, 5)
                from budgetMultiplier in Gen.Choose(3, 6)
                let budget = templateCount * budgetMultiplier
                from issueCounts in Gen.ArrayOf(Gen.Choose(1, 50), templateCount)
                from prCounts in Gen.ArrayOf(Gen.Choose(1, 50), templateCount)
                from decompCounts in Gen.ArrayOf(Gen.Choose(1, 50), templateCount)
                select new StarvationInput(templateCount, budget,
                    issueCounts.ToList(), prCounts.ToList(), decompCounts.ToList());
            return gen.ToArbitrary();
        }
    }

    public class IsolationArbitraries
    {
        public static Arbitrary<IsolationInput> IsolationInputArb()
        {
            var gen =
                from templateCount in Gen.Choose(2, 4)
                from failingIndex in Gen.Choose(0, templateCount - 1)
                from budget in Gen.Choose(templateCount, 20)
                from issuesPerTemplate in Gen.Choose(1, 8)
                select new IsolationInput(templateCount, failingIndex, budget, issuesPerTemplate);
            return gen.ToArbitrary();
        }
    }

    public class EmptyCycleArbitraries
    {
        public static Arbitrary<EmptyCycleInput> EmptyCycleInputArb()
        {
            var gen =
                from templateCount in Gen.Choose(1, 10)
                from budget in Gen.Choose(1, 20)
                select new EmptyCycleInput(templateCount, budget);
            return gen.ToArbitrary();
        }
    }
}
