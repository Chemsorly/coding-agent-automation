using FsCheck;
using FsCheck.Fluent;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// FsCheck generators for PipelineRunLifecycleService property tests.
/// Feature: 017-pipeline-run-lifecycle-service
/// </summary>
public class PipelineRunLifecycleArbitraries
{
    private static readonly PipelineStep[] AllSteps = Enum.GetValues<PipelineStep>();
    private static readonly PipelineStep[] TerminalSteps = [PipelineStep.Completed, PipelineStep.Failed, PipelineStep.Cancelled];
    private static readonly PipelineStep[] NonTerminalSteps = AllSteps.Except(TerminalSteps).ToArray();

    private static readonly string[] RunIds = ["run-1", "run-2", "run-3", "run-abc", "run-xyz"];
    private static readonly string[] IssueIds = ["issue-1", "issue-2", "issue-42", "PROJ-100", "BUG-7"];
    private static readonly string[] IssueTitles = ["Fix bug", "Add feature", "Refactor code", "Update docs"];
    private static readonly string[] FailureReasons =
    [
        "Build failed: compilation error",
        "Tests failed: 3 tests failed",
        "Agent timeout after 300s",
        "Connection refused",
        "Quality gates failed after max retries"
    ];
    private static readonly string[] OutputLines =
    [
        "🚀 Starting pipeline...",
        "✅ Clone complete",
        "📝 Analyzing code...",
        "❌ Build failed",
        "🧪 Running tests...",
        "⏱️ Step completed in 5.2s"
    ];
    private static readonly string[] SessionIds = ["session-1", "session-abc", "chat-42", "s-xyz-123"];

    /// <summary>Generates a PipelineRun with a random step.</summary>
    public static Arbitrary<LifecycleTestInput> LifecycleTestInputArb()
    {
        var gen =
            from step in Gen.Elements(AllSteps)
            from runId in Gen.Elements(RunIds)
            from issueId in Gen.Elements(IssueIds)
            from issueTitle in Gen.Elements(IssueTitles)
            from hasActiveRun in Gen.Elements(true, false)
            select new LifecycleTestInput
            {
                Step = step,
                RunId = runId,
                IssueId = issueId,
                IssueTitle = issueTitle,
                HasActiveRun = hasActiveRun
            };

        return gen.ToArbitrary();
    }

    /// <summary>Generates input for HasAnyActiveRuns tests.</summary>
    public static Arbitrary<HasActiveRunsInput> HasActiveRunsInputArb()
    {
        var gen =
            from step in Gen.Elements(AllSteps)
            from hasLocalRun in Gen.Elements(true, false)
            from runServiceHasActive in Gen.Elements(true, false)
            select new HasActiveRunsInput
            {
                Step = step,
                HasLocalRun = hasLocalRun,
                RunServiceHasActive = runServiceHasActive
            };

        return gen.ToArbitrary();
    }

    /// <summary>Generates input for GetAllActiveRuns tests.</summary>
    public static Arbitrary<GetAllActiveRunsInput> GetAllActiveRunsInputArb()
    {
        var gen =
            from hasLocalRun in Gen.Elements(true, false)
            from agentRunCount in Gen.Choose(0, 4)
            from localStep in Gen.Elements(NonTerminalSteps)
            select new GetAllActiveRunsInput
            {
                HasLocalRun = hasLocalRun,
                AgentRunCount = agentRunCount,
                LocalStep = localStep
            };

        return gen.ToArbitrary();
    }

    /// <summary>Generates input for IsIssueBeingProcessed tests.</summary>
    public static Arbitrary<IsIssueBeingProcessedInput> IsIssueBeingProcessedInputArb()
    {
        var gen =
            from queryIssue in Gen.Elements(IssueIds)
            from localStep in Gen.Elements(AllSteps)
            from localMatchesQuery in Gen.Elements(true, false)
            from runServiceReports in Gen.Elements(true, false)
            select new IsIssueBeingProcessedInput
            {
                QueryIssue = queryIssue,
                LocalStep = localStep,
                LocalMatchesQuery = localMatchesQuery,
                RunServiceReports = runServiceReports
            };

        return gen.ToArbitrary();
    }

    /// <summary>Generates input for event emission tests.</summary>
    public static Arbitrary<EventEmissionInput> EventEmissionInputArb()
    {
        var gen =
            from outputLine in Gen.Elements(OutputLines)
            from sessionId in Gen.Elements(SessionIds)
            from exitCode in Gen.Choose(0, 255)
            select new EventEmissionInput
            {
                OutputLine = outputLine,
                SessionId = sessionId,
                ExitCode = exitCode
            };

        return gen.ToArbitrary();
    }

    /// <summary>Generates input for TransitionTo tests.</summary>
    public static Arbitrary<TransitionInput> TransitionInputArb()
    {
        var gen =
            from initialStep in Gen.Elements(AllSteps)
            from targetStep in Gen.Elements(AllSteps)
            select new TransitionInput
            {
                InitialStep = initialStep,
                TargetStep = targetStep
            };

        return gen.ToArbitrary();
    }

    /// <summary>Generates input for FailRunAsync tests.</summary>
    public static Arbitrary<FailRunInput> FailRunInputArb()
    {
        var gen =
            from step in Gen.Elements(NonTerminalSteps)
            from reason in Gen.Elements(FailureReasons)
            from runId in Gen.Elements(RunIds)
            select new FailRunInput
            {
                Step = step,
                Reason = reason,
                RunId = runId
            };

        return gen.ToArbitrary();
    }

    /// <summary>Generates input for CancelPipelineAsync tests.</summary>
    public static Arbitrary<CancelRunInput> CancelRunInputArb()
    {
        var gen =
            from step in Gen.Elements(NonTerminalSteps)
            from runId in Gen.Elements(RunIds)
            select new CancelRunInput
            {
                Step = step,
                RunId = runId
            };

        return gen.ToArbitrary();
    }

    /// <summary>Generates input for MarkAgentRunsCancelled tests.</summary>
    public static Arbitrary<MarkAgentRunsCancelledInput> MarkAgentRunsCancelledInputArb()
    {
        var gen =
            from agentRunCount in Gen.Choose(1, 5)
            select new MarkAgentRunsCancelledInput
            {
                AgentRunCount = agentRunCount
            };

        return gen.ToArbitrary();
    }

    /// <summary>Generates input for RegisterDispatchedRun tests.</summary>
    public static Arbitrary<RegisterRunInput> RegisterRunInputArb()
    {
        var gen =
            from runId in Gen.Elements(RunIds)
            from issueId in Gen.Elements(IssueIds)
            from issueTitle in Gen.Elements(IssueTitles)
            select new RegisterRunInput
            {
                RunId = runId,
                IssueId = issueId,
                IssueTitle = issueTitle
            };

        return gen.ToArbitrary();
    }
}

// ── Input record types for property tests ───────────────────────────────

public record LifecycleTestInput
{
    public PipelineStep Step { get; init; }
    public string RunId { get; init; } = "";
    public string IssueId { get; init; } = "";
    public string IssueTitle { get; init; } = "";
    public bool HasActiveRun { get; init; }
}

public record HasActiveRunsInput
{
    public PipelineStep Step { get; init; }
    public bool HasLocalRun { get; init; }
    public bool RunServiceHasActive { get; init; }
}

public record GetAllActiveRunsInput
{
    public bool HasLocalRun { get; init; }
    public int AgentRunCount { get; init; }
    public PipelineStep LocalStep { get; init; }
}

public record IsIssueBeingProcessedInput
{
    public string QueryIssue { get; init; } = "";
    public PipelineStep LocalStep { get; init; }
    public bool LocalMatchesQuery { get; init; }
    public bool RunServiceReports { get; init; }
}

public record EventEmissionInput
{
    public string OutputLine { get; init; } = "";
    public string SessionId { get; init; } = "";
    public int ExitCode { get; init; }
}

public record TransitionInput
{
    public PipelineStep InitialStep { get; init; }
    public PipelineStep TargetStep { get; init; }
}

public record FailRunInput
{
    public PipelineStep Step { get; init; }
    public string Reason { get; init; } = "";
    public string RunId { get; init; } = "";
}

public record CancelRunInput
{
    public PipelineStep Step { get; init; }
    public string RunId { get; init; } = "";
}

public record MarkAgentRunsCancelledInput
{
    public int AgentRunCount { get; init; }
}

public record RegisterRunInput
{
    public string RunId { get; init; } = "";
    public string IssueId { get; init; } = "";
    public string IssueTitle { get; init; } = "";
}
