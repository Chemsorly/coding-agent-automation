using System.Text.Json;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Fakes;

/// <summary>
/// Agent provider that executes pre-configured scripts. Each ExecuteAsync call pops the next
/// script from the queue, writes expected files to the workspace, and returns the configured result.
/// </summary>
public sealed class ScriptedAgentProvider : IAgentProvider
{
    private readonly Queue<AgentScript> _scripts = new();

    public AgentProviderType ProviderType => AgentProviderType.KiroCli;

    /// <summary>
    /// When set, ExecuteAsync will await this before proceeding.
    /// Use for cancellation tests or observing intermediate states.
    /// </summary>
    public TaskCompletionSource? PauseBeforeExecution { get; set; }

    public void Reset()
    {
        _scripts.Clear();
        PauseBeforeExecution = null;
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

    public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct, Action<string>? onOutputLine = null)
    {
        if (PauseBeforeExecution is not null)
            await PauseBeforeExecution.Task;

        ct.ThrowIfCancellationRequested();

        if (_scripts.Count == 0)
            return new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() };

        var script = _scripts.Dequeue();
        script.Execute(request.WorkspacePath);

        onOutputLine?.Invoke($"[ScriptedAgent] Executed {script.GetType().Name}");

        return new AgentResult { ExitCode = script.ExitCode, OutputLines = Array.Empty<string>() };
    }

    public Task EnsureSessionAsync(string workspacePath, CancellationToken ct) => Task.CompletedTask;
    public AgentHealthStatus GetHealthStatus() => new() { IsExecuting = false };
    public Task KillAsync() => Task.CompletedTask;
    public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<string?> GetLatestSessionIdAsync(string workspacePath, CancellationToken ct) => Task.FromResult<string?>("fake-session-id");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Base class for agent scripts.</summary>
public abstract class AgentScript
{
    public int ExitCode { get; init; }
    public abstract void Execute(string workspacePath);
}

/// <summary>Writes analysis.md and analysis-assessment.json to .kiro/ directory.</summary>
public sealed class AnalysisScript : AgentScript
{
    public string Recommendation { get; init; } = "ready";
    public string Reason { get; init; } = "Test analysis";
    public string AnalysisContent { get; init; } = new string('x', 200);

    public override void Execute(string workspacePath)
    {
        var kiroDir = Path.Combine(workspacePath, ".kiro");
        Directory.CreateDirectory(kiroDir);

        File.WriteAllText(Path.Combine(kiroDir, "analysis.md"), AnalysisContent);

        var assessment = new
        {
            recommendation = Recommendation,
            reason = Reason,
            concerns = Array.Empty<string>(),
            blockingIssues = Array.Empty<string>()
        };
        File.WriteAllText(
            Path.Combine(kiroDir, "analysis-assessment.json"),
            JsonSerializer.Serialize(assessment, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}

/// <summary>Simple code generation script (no file writes needed — pipeline checks exit code).</summary>
public sealed class CodeGenScript : AgentScript
{
    public override void Execute(string workspacePath) { }
}

/// <summary>Writes review findings to .kiro/review-findings.md.</summary>
public sealed class ReviewScript : AgentScript
{
    public string Findings { get; init; } = "No findings.";

    public override void Execute(string workspacePath)
    {
        var kiroDir = Path.Combine(workspacePath, ".kiro");
        Directory.CreateDirectory(kiroDir);
        File.WriteAllText(Path.Combine(kiroDir, "review-findings.md"), Findings);
    }
}
