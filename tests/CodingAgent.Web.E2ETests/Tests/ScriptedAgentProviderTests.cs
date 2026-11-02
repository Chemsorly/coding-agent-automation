using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Unit-style tests for <see cref="ScriptedAgentProvider"/> that exercise the new
/// failure, usage, pause, and file-writing capabilities added in issue #4098.
/// Each test creates its own <see cref="ScriptedAgentProvider"/> and a temporary workspace
/// directory; no shared fixture is used.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class ScriptedAgentProviderTests
{
    // ── helpers ──────────────────────────────────────────────────────────────────

    private static string CreateTempWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteWorkspace(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static AgentRequest MakeRequest(string workspace, string prompt = "Test prompt") =>
        new() { Prompt = prompt, WorkspacePath = workspace };

    // ── tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ScriptWithFailureFields_ReturnsThemInResult()
    {
        var provider = new ScriptedAgentProvider();
        var workspace = CreateTempWorkspace();
        try
        {
            provider.Enqueue(new CodeGenScript
            {
                ExitCode = 2,
                ErrorCategory = AgentErrorCategory.ProviderBillingLimit,
                OutputLines = ["line-1"],
                Usage = new TokenUsage { InputTokens = 5 },
                Cost = 0.5m
            });

            var captured = new List<string>();
            var result = await provider.ExecuteAsync(
                MakeRequest(workspace), CancellationToken.None, captured.Add);

            result.ExitCode.Should().Be(2);
            result.Success.Should().BeFalse();
            result.ErrorCategory.Should().Be(AgentErrorCategory.ProviderBillingLimit);
            result.OutputLines.Should().Contain("line-1");
            result.Usage!.InputTokens.Should().Be(5);
            result.Cost.Should().Be(0.5m);
            captured.Should().Contain("line-1");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ScriptWithThrow_ThrowsAndWritesNoFile()
    {
        var provider = new ScriptedAgentProvider();
        var workspace = CreateTempWorkspace();
        try
        {
            provider.Enqueue(new AnalysisScript
            {
                ThrowOnExecute = new InvalidOperationException("boom")
            });

            var act = () => provider.ExecuteAsync(MakeRequest(workspace), CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

            // AnalysisScript would have written .agent/analysis.md — it must not exist
            File.Exists(Path.Combine(workspace, ".agent", "analysis.md"))
                .Should().BeFalse("ThrowOnExecute fires before script.Execute, so no files are written");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Fact]
    public async Task ExecuteAsync_EmptyQueue_RecordsUnscriptedCallWithNullUsage()
    {
        var provider = new ScriptedAgentProvider();
        var workspace = CreateTempWorkspace();
        try
        {
            const string prompt = "Unscripted test prompt";
            var result = await provider.ExecuteAsync(
                MakeRequest(workspace, prompt), CancellationToken.None);

            result.ExitCode.Should().Be(0);
            result.Usage.Should().BeNull("unscripted calls return null Usage to avoid session-restart in the fix loop");

            provider.Calls.Should().HaveCount(1);
            provider.UnscriptedCalls.Should().HaveCount(1);
            provider.UnscriptedCalls[0].Request.Prompt.Should().Be(prompt);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PauseUntil_HonoursCancellation()
    {
        var provider = new ScriptedAgentProvider();
        var workspace = CreateTempWorkspace();
        try
        {
            var pauseTcs = new TaskCompletionSource();
            provider.Enqueue(new CodeGenScript { PauseUntil = pauseTcs });

            using var cts = new CancellationTokenSource();
            var executeTask = provider.ExecuteAsync(MakeRequest(workspace), cts.Token);

            // Wait until the call has been recorded (the call is recorded before the pause begins)
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (provider.Calls.Count < 1 && DateTime.UtcNow < deadline)
                await Task.Delay(5);
            provider.Calls.Count.Should().BeGreaterThanOrEqualTo(1, "the call must have started before we cancel");

            cts.Cancel();

            // WaitAsync(5s) ensures the test fails with a clear TimeoutException rather than hanging
            // if cancellation is accidentally broken (e.g. WaitAsync(ct) reverted to plain await).
            Func<Task> act = () => executeTask.WaitAsync(TimeSpan.FromSeconds(5));
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PauseBeforeExecution_HonoursCancellation()
    {
        var provider = new ScriptedAgentProvider();
        var workspace = CreateTempWorkspace();
        try
        {
            provider.PauseBeforeExecution = new TaskCompletionSource();
            provider.Enqueue(new CodeGenScript());

            using var cts = new CancellationTokenSource();
            var executeTask = provider.ExecuteAsync(MakeRequest(workspace), cts.Token);

            // Wait until the call has been recorded (recorded before PauseBeforeExecution is awaited)
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (provider.Calls.Count < 1 && DateTime.UtcNow < deadline)
                await Task.Delay(5);
            provider.Calls.Count.Should().BeGreaterThanOrEqualTo(1, "the call must have started before we cancel");

            cts.Cancel();

            // WaitAsync(5s) ensures the test fails with a clear TimeoutException rather than hanging
            // if cancellation is accidentally broken.
            Func<Task> act = () => executeTask.WaitAsync(TimeSpan.FromSeconds(5));
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Fact]
    public async Task ValidateAsync_WithValidateException_Throws()
    {
        var provider = new ScriptedAgentProvider();
        var ex = new InvalidOperationException("validate-fail");
        provider.ValidateException = ex;

        var act = () => provider.ValidateAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("validate-fail");
    }

    [Fact]
    public async Task Reset_ClearsScriptsCallsAndValidateException()
    {
        var provider = new ScriptedAgentProvider();
        var workspace = CreateTempWorkspace();
        try
        {
            var request = MakeRequest(workspace);

            // Enqueue two scripts but only consume one, leaving one in the queue at Reset time.
            // This ensures _scripts.Clear() in Reset() is not a no-op.
            provider.EnqueueCodeGen();
            provider.EnqueueCodeGen(); // intentionally NOT consumed before Reset
            provider.ValidateException = new Exception("fail");

            // Consume the first script only
            await provider.ExecuteAsync(request, CancellationToken.None);
            provider.Calls.Should().HaveCount(1);

            provider.Reset();

            // All recorded calls cleared
            provider.Calls.Should().BeEmpty("Reset must clear all recorded calls");
            provider.UnscriptedCalls.Should().BeEmpty("Reset must clear all recorded calls");
            provider.ValidateException.Should().BeNull("Reset must clear ValidateException");

            // The unconsumed second script must also be gone: this call finds an empty queue
            await provider.ExecuteAsync(request, CancellationToken.None);
            provider.UnscriptedCalls.Should().HaveCount(1,
                "Reset cleared the remaining script, so the first post-Reset call is unscripted");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Fact]
    public async Task SubIssueFilesScript_WritesFilesTheRealParserAccepts()
    {
        var provider = new ScriptedAgentProvider();
        var workspace = CreateTempWorkspace();
        try
        {
            const string title1 = "Add input validation";
            const string title2 = "Fix null reference in payment handler";

            provider.EnqueueSubIssues(
                new SubIssueFile(title1, "Validate all inputs before processing."),
                new SubIssueFile(title2, "Handle null reference in payment flow.", Dependencies: [title1])
            );

            await provider.ExecuteAsync(MakeRequest(workspace), CancellationToken.None);

            var proposals = await SubIssueFileParser.ParseSubIssueFilesAsync(
                workspace, Serilog.Log.Logger, CancellationToken.None);

            proposals.Should().HaveCount(2);
            proposals[0].Title.Should().Be(title1);
            proposals[1].Title.Should().Be(title2);
            proposals[1].Dependencies.Should().Contain(title1);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Fact]
    public async Task TriageResultScript_WritesAResultTheRealParserAndValidatorAccept()
    {
        var provider = new ScriptedAgentProvider();
        var workspace = CreateTempWorkspace();
        try
        {
            provider.EnqueueTriageResult(
                verdict: "cause_found",
                summary: "Root cause identified in payment service.");

            await provider.ExecuteAsync(MakeRequest(workspace), CancellationToken.None);

            var resultPath = Path.Combine(workspace, AgentWorkspacePaths.TriageResultFilePath);
            var json = await File.ReadAllTextAsync(resultPath);

            // Parse — assert no error before accessing Result
            var parseOutcome = TriageResultParser.Parse(json);
            parseOutcome.Error.Should().BeNull("the triage result file must be valid JSON with a known verdict");
            parseOutcome.Result!.Verdict.Should().Be(TriageVerdict.CauseFound);

            // Validate — assert no error before accessing Result
            var validationOutcome = TriageResultValidator.Validate(parseOutcome.Result!, [], null);
            validationOutcome.Error.Should().BeNull("the triage result must pass validation");
            validationOutcome.Result!.Verdict.Should().Be(TriageVerdict.CauseFound);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }
}
