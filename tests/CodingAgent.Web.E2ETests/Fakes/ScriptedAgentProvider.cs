using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Fakes;

/// <summary>
/// Represents a single call to <see cref="ScriptedAgentProvider.ExecuteAsync"/>.
/// </summary>
public sealed record AgentCall(AgentRequest Request, bool Scripted);

/// <summary>A sub-issue to be written by <see cref="SubIssueFilesScript"/>.</summary>
public sealed record SubIssueFile(
    string Title,
    string Body,
    IReadOnlyList<string>? Dependencies = null,
    IReadOnlyList<string>? Labels = null,
    string? TargetRepository = null);

/// <summary>
/// Agent provider that executes pre-configured scripts. Each ExecuteAsync call pops the next
/// script from the queue, writes expected files to the workspace, and returns the configured result.
/// </summary>
public sealed class ScriptedAgentProvider : IAgentProvider
{
    private readonly Queue<AgentScript> _scripts = new();
    private readonly ConcurrentQueue<AgentCall> _calls = new();

    public AgentProviderType ProviderType => AgentProviderType.KiroCli;

    public string? Model => null;

    public string McpConfigPath => Path.Combine(Path.GetTempPath(), ".kiro", "settings", "mcp.json");

    public bool SupportsParallelExecution => false;

    public bool SupportsVisionInput => true;

    public IReadOnlyList<string> PipelineInjectedPaths { get; } = [".kiro"];

    /// <summary>
    /// When set, ExecuteAsync will await this before proceeding.
    /// Use for cancellation tests or observing intermediate states.
    /// </summary>
    public TaskCompletionSource? PauseBeforeExecution { get; set; }

    /// <summary>Every ExecuteAsync call, in order. Poll it to see that a call has started.</summary>
    public IReadOnlyList<AgentCall> Calls => _calls.ToList();

    /// <summary>The calls that found an empty queue. Tests assert this is empty.</summary>
    public IReadOnlyList<AgentCall> UnscriptedCalls => _calls.Where(c => !c.Scripted).ToList();

    /// <summary>When set, ValidateAsync throws it. For pre-flight failures.</summary>
    public Exception? ValidateException { get; set; }

    public void Reset()
    {
        _scripts.Clear();
        PauseBeforeExecution = null;
        while (_calls.TryDequeue(out _)) { }
        ValidateException = null;
    }

    /// <summary>Enqueues a script to be executed on the next ExecuteAsync call.</summary>
    public ScriptedAgentProvider Enqueue(AgentScript script)
    {
        _scripts.Enqueue(script);
        return this;
    }

    /// <summary>Enqueues an analysis script that writes a "ready" assessment.</summary>
    public ScriptedAgentProvider EnqueueReadyAnalysis(string content = "Analysis complete.")
    {
        _scripts.Enqueue(new AnalysisScript
        {
            Recommendation = "ready",
            AnalysisContent = content.Length < 200 ? new string('x', 200) : content
        });
        return this;
    }

    /// <summary>Enqueues a successful code generation script.</summary>
    public ScriptedAgentProvider EnqueueCodeGen(int exitCode = 0)
    {
        _scripts.Enqueue(new CodeGenScript { ExitCode = exitCode });
        return this;
    }

    /// <summary>Enqueues an analysis script with a specific recommendation.</summary>
    public ScriptedAgentProvider EnqueueAnalysis(string recommendation, string? reason = null)
    {
        _scripts.Enqueue(new AnalysisScript
        {
            Recommendation = recommendation,
            Reason = reason ?? $"Test {recommendation}",
            AnalysisContent = new string('x', 200)
        });
        return this;
    }

    /// <summary>Enqueues a decomposition plan script for Phase 1 decomposition runs.</summary>
    public ScriptedAgentProvider EnqueueDecompositionPlan(string? content = null)
    {
        _scripts.Enqueue(new DecompositionPlanScript
        {
            PlanContent = content ?? new string('x', 200)
        });
        return this;
    }

    /// <summary>
    /// Enqueues a brain reflection script that writes <c>.agent/brain-candidates.md</c>
    /// with <paramref name="text"/> to the workspace path (spec 054, 10.1).
    /// </summary>
    public ScriptedAgentProvider EnqueueBrainCandidates(string text)
    {
        _scripts.Enqueue(new BrainCandidatesScript { CandidatesText = text });
        return this;
    }

    /// <summary>Enqueues a script that writes a file at the given workspace-relative path.</summary>
    public ScriptedAgentProvider EnqueueFile(string relativePath, string content)
    {
        _scripts.Enqueue(new WriteFileScript { RelativePath = relativePath, Content = content });
        return this;
    }

    /// <summary>Enqueues a script that writes sub-issue JSON files under <c>.agent/sub-issues/</c>.</summary>
    public ScriptedAgentProvider EnqueueSubIssues(params SubIssueFile[] files)
    {
        _scripts.Enqueue(new SubIssueFilesScript { Files = files });
        return this;
    }

    /// <summary>Enqueues a script that writes a triage result JSON file.</summary>
    public ScriptedAgentProvider EnqueueTriageResult(string verdict = "not_a_bug", string summary = "Scripted triage summary")
    {
        _scripts.Enqueue(new TriageResultScript { Verdict = verdict, Summary = summary });
        return this;
    }

    public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct, Action<string>? onOutputLine = null)
    {
        // TODO: _scripts.Count and _scripts.TryDequeue are not atomic. Concurrent calls could produce
        // incorrect Scripted flags (both threads read Count > 0, one gets the script, the other gets false
        // from TryDequeue but was already recorded as Scripted: true). In practice SupportsParallelExecution
        // = false prevents overlapping calls, so this is a theoretical concern. If that changes, _scripts
        // should become a ConcurrentQueue<AgentScript>.
        _calls.Enqueue(new AgentCall(request, Scripted: _scripts.Count > 0));

        if (PauseBeforeExecution is not null)
            await PauseBeforeExecution.Task.WaitAsync(ct);

        ct.ThrowIfCancellationRequested();

        // Usage stays null here on purpose: a zero-token result restarts the session in the fix loop.
        if (!_scripts.TryDequeue(out var script))
            return new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() };

        if (script.PauseUntil is not null)
            await script.PauseUntil.Task.WaitAsync(ct);

        if (script.ThrowOnExecute is not null)
            throw script.ThrowOnExecute;

        script.Execute(request.WorkspacePath);

        onOutputLine?.Invoke($"[ScriptedAgent] Executed {script.GetType().Name}");
        foreach (var line in script.OutputLines)
            onOutputLine?.Invoke(line);

        return new AgentResult
        {
            ExitCode = script.ExitCode,
            OutputLines = script.OutputLines,
            Usage = script.Usage,
            Cost = script.Cost,
            ErrorCategory = script.ErrorCategory
        };
    }

    public Task EnsureSessionAsync(WorkspacePath workspacePath, CancellationToken ct) => Task.CompletedTask;
    public AgentHealthStatus GetHealthStatus() => new() { IsExecuting = false };
    public Task KillAsync() => Task.CompletedTask;

    public Task ValidateAsync(CancellationToken ct)
    {
        if (ValidateException is not null)
            throw ValidateException;
        return Task.CompletedTask;
    }

    public Task<string?> GetLatestSessionIdAsync(WorkspacePath workspacePath, CancellationToken ct) => Task.FromResult<string?>("fake-session-id");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Base class for agent scripts.</summary>
public abstract class AgentScript
{
    public int ExitCode { get; init; }
    public AgentErrorCategory ErrorCategory { get; init; } = AgentErrorCategory.None;
    public IReadOnlyList<string> OutputLines { get; init; } = [];
    public TokenUsage? Usage { get; init; }
    public decimal? Cost { get; init; }
    /// <summary>When set, ExecuteAsync throws this after the pause and writes no files.</summary>
    public Exception? ThrowOnExecute { get; init; }
    /// <summary>When set, ExecuteAsync waits for it, honouring the cancellation token. For cancelling mid-call.</summary>
    public TaskCompletionSource? PauseUntil { get; init; }
    public abstract void Execute(string workspacePath);
}

/// <summary>Writes analysis.md and analysis-assessment.json to .agent/ directory.</summary>
public sealed class AnalysisScript : AgentScript
{
    public string Recommendation { get; init; } = "ready";
    public string Reason { get; init; } = "Test analysis";
    public string AnalysisContent { get; init; } = new string('x', 200);

    public override void Execute(string workspacePath)
    {
        var agentDir = Path.Combine(workspacePath, ".agent");
        Directory.CreateDirectory(agentDir);

        File.WriteAllText(Path.Combine(agentDir, "analysis.md"), AnalysisContent);

        var assessment = new
        {
            recommendation = Recommendation,
            reason = Reason,
            concerns = Array.Empty<string>(),
            blockingIssues = Array.Empty<string>()
        };
        File.WriteAllText(
            Path.Combine(agentDir, "analysis-assessment.json"),
            JsonSerializer.Serialize(assessment, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}

/// <summary>Simple code generation script (no file writes needed — pipeline checks exit code).</summary>
public sealed class CodeGenScript : AgentScript
{
    public override void Execute(string workspacePath) { }
}

/// <summary>Writes review findings to .agent/review-findings.md.</summary>
public sealed class ReviewScript : AgentScript
{
    public string Findings { get; init; } = "No findings.";

    public override void Execute(string workspacePath)
    {
        var agentDir = Path.Combine(workspacePath, ".agent");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "review-findings.md"), Findings);
    }
}

/// <summary>
/// Writes a decomposition plan file to .agent/decomposition-plan.md so the
/// DecompositionAnalysisStep validates and advances the run.
/// Also writes the adversarial review file so the review sub-step succeeds.
/// </summary>
public sealed class DecompositionPlanScript : AgentScript
{
    public string PlanContent { get; init; } = new string('x', 200);

    public override void Execute(string workspacePath)
    {
        var agentDir = Path.Combine(workspacePath, ".agent");
        Directory.CreateDirectory(agentDir);
        // Primary plan file the pipeline validates
        File.WriteAllText(Path.Combine(agentDir, "decomposition-plan.md"), PlanContent);
        // Review agent output (adversarial review sub-step)
        File.WriteAllText(Path.Combine(agentDir, "decomposition-review.md"), "No blocking issues found.");
    }
}

/// <summary>
/// Writes brain-candidates.md to .agent/ so the reflection step can read it (spec 054, 10.1).
/// </summary>
public sealed class BrainCandidatesScript : AgentScript
{
    public string CandidatesText { get; init; } = "- a candidate lesson\n";

    public override void Execute(string workspacePath)
    {
        var candidatesPath = Path.Combine(workspacePath,
            AgentWorkspacePaths.BrainCandidatesFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(candidatesPath)!);
        File.WriteAllText(candidatesPath, CandidatesText);
    }
}

/// <summary>Writes a file at the given workspace-relative path, creating directories as needed.</summary>
public sealed class WriteFileScript : AgentScript
{
    public required string RelativePath { get; init; }
    public required string Content { get; init; }

    public override void Execute(string workspacePath)
    {
        var fullPath = Path.Combine(workspacePath, RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, Content);
    }
}

/// <summary>
/// Writes sub-issue JSON files under <c>.agent/sub-issues/</c> in the format expected by
/// <see cref="CodingAgent.Pipeline.Services.SubIssueFileParser"/>.
/// </summary>
public sealed class SubIssueFilesScript : AgentScript
{
    public required IReadOnlyList<SubIssueFile> Files { get; init; }

    public override void Execute(string workspacePath)
    {
        var dir = Path.Combine(workspacePath, AgentWorkspacePaths.SubIssuesDirectory);
        Directory.CreateDirectory(dir);

        for (var i = 0; i < Files.Count; i++)
        {
            var file = Files[i];
            var rawSlug = Regex.Replace(file.Title.ToLowerInvariant(), @"[^a-z0-9]+", "-");
            var slug = rawSlug.Length > 60 ? rawSlug[..60] : rawSlug;
            var fileName = $"{i + 1:00}-{slug}.json";

            var obj = new Dictionary<string, object>
            {
                ["title"] = file.Title,
                ["body"] = file.Body,
                ["dependencies"] = file.Dependencies?.ToArray() ?? Array.Empty<string>(),
                ["labels"] = file.Labels?.ToArray() ?? Array.Empty<string>()
            };
            if (file.TargetRepository is not null)
                obj["targetRepository"] = file.TargetRepository;

            File.WriteAllText(
                Path.Combine(dir, fileName),
                JsonSerializer.Serialize(obj));
        }
    }
}

/// <summary>
/// Writes a triage result JSON file at <see cref="AgentWorkspacePaths.TriageResultFilePath"/>
/// in the format expected by <see cref="CodingAgent.Pipeline.Services.TriageResultParser"/>.
/// </summary>
public sealed class TriageResultScript : AgentScript
{
    public string Verdict { get; init; } = "not_a_bug";
    public string Summary { get; init; } = "Scripted triage summary";
    public string? RawJson { get; init; }

    public override void Execute(string workspacePath)
    {
        var fullPath = Path.Combine(workspacePath, AgentWorkspacePaths.TriageResultFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // TODO: The RawJson branch is not exercised by ScriptedAgentProviderTests (tracked for a follow-up
        // issue). To cover it: enqueue a TriageResultScript with RawJson set to a valid JSON string and
        // verify the file content matches verbatim.
        if (RawJson is not null)
        {
            File.WriteAllText(fullPath, RawJson);
            return;
        }

        var investigated = new[]
        {
            new Dictionary<string, string>
            {
                ["check"] = "Scripted check",
                ["where"] = "scripted",
                ["for"] = "symptom",
                ["result"] = "Scripted result"
            }
        };

        var obj = new Dictionary<string, object>
        {
            ["verdict"] = Verdict,
            ["summary"] = Summary,
            ["confidence"] = "high",
            ["investigated"] = investigated
        };

        File.WriteAllText(fullPath, JsonSerializer.Serialize(obj));
    }
}
