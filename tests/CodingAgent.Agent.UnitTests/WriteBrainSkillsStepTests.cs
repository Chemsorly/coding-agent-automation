using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;
using Xunit;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="WriteBrainSkillsStep"/>.
/// Spec 054, req 2.1-2.2, 5.4. Always uses temp directories (T8).
/// </summary>
public class WriteBrainSkillsStepTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workspacePath;
    private readonly string _claudeRulesDir;
    private readonly string _openCodeDir;
    private readonly PipelineConfiguration _config = new();

    public WriteBrainSkillsStepTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"wbs-{Guid.NewGuid():N}");
        _workspacePath = Path.Combine(_tempRoot, "workspace");
        _claudeRulesDir = Path.Combine(_tempRoot, "claude-rules");
        _openCodeDir = Path.Combine(_tempRoot, "opencode");
        Directory.CreateDirectory(_workspacePath);
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".kiro", "steering"));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempRoot)) return;
        foreach (var f in Directory.GetFiles(_tempRoot, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_tempRoot, recursive: true);
    }

    private PipelineStepContext BuildContext(AgentProviderType providerType, string? brainSkillsText)
    {
        var run = new PipelineRun
        {
            RunId = "r1", IssueIdentifier = "o/r#1", IssueTitle = "T",
            IssueProviderConfigId = "ip", RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow, WorkspacePath = _workspacePath,
        };
        if (brainSkillsText is not null)
            run.BrainSkills = new BrainSkillsInjection(
                brainSkillsText, BrainSkillsResults.Injected, brainSkillsText.Length,
                ["projects/r/SKILL.md"], "abc123");

        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.ProviderType).Returns(providerType);

        var prOrchestrator = new PullRequestOrchestrator(Serilog.Log.Logger);

        return new PipelineStepContext
        {
            Run = run,
            Config = _config,
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = mockAgent.Object,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = Mock.Of<IConfigurationStore>(),
            IssueProvider = null,
            Callbacks = new NoOpCallbacks(),
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = new AgentPhaseExecutor(Serilog.Log.Logger),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = prOrchestrator,
            Logger = Serilog.Log.Logger,
        };
    }

    // ── Kiro CLI ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kiro_WithBrainSkills_WritesBrainFile()
    {
        var step = new WriteBrainSkillsStep();
        var ctx = BuildContext(AgentProviderType.KiroCli, "- [2+] For X, do Y (#1).");

        await step.ExecuteAsync(ctx, CancellationToken.None);

        var brainFile = Path.Combine(_workspacePath, AgentWorkspacePaths.KiroSteeringBrainFilePath);
        File.Exists(brainFile).Should().BeTrue();
        var content = File.ReadAllText(brainFile);
        content.Should().Contain("inclusion: always");
        content.Should().Contain("- [2+] For X, do Y (#1).");
    }

    [Fact]
    public async Task Kiro_FileHasInclusionAlwaysFrontMatter()
    {
        var step = new WriteBrainSkillsStep();
        var ctx = BuildContext(AgentProviderType.KiroCli, "- [2+] For X, do Y (#1).");

        await step.ExecuteAsync(ctx, CancellationToken.None);

        var content = File.ReadAllText(Path.Combine(_workspacePath, AgentWorkspacePaths.KiroSteeringBrainFilePath));
        content.Should().Contain("inclusion: always");
    }

    [Fact]
    public async Task Kiro_WithNoBrainSkills_RemovesStaleFile()
    {
        var brainFile = Path.Combine(_workspacePath, AgentWorkspacePaths.KiroSteeringBrainFilePath);
        File.WriteAllText(brainFile, "stale");

        var step = new WriteBrainSkillsStep();
        var ctx = BuildContext(AgentProviderType.KiroCli, brainSkillsText: null);

        await step.ExecuteAsync(ctx, CancellationToken.None);

        File.Exists(brainFile).Should().BeFalse("stale file should be removed when no skills");
    }

    // ── Claude Code ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ClaudeCode_WithBrainSkills_WritesToRulesDirectory()
    {
        var step = new WriteBrainSkillsStep(claudeRulesDirectory: _claudeRulesDir);
        var ctx = BuildContext(AgentProviderType.ClaudeCode, "- [2+] For X, do Y (#1).");

        await step.ExecuteAsync(ctx, CancellationToken.None);

        var brainFile = Path.Combine(_claudeRulesDir, ClaudeSteeringFiles.BrainFileName);
        File.Exists(brainFile).Should().BeTrue();
        File.ReadAllText(brainFile).Should().Contain("- [2+] For X, do Y (#1).");
    }

    [Fact]
    public async Task ClaudeCode_RulesDirectoryDoesNotExist_CreatesAndWritesFile()
    {
        var newDir = Path.Combine(_tempRoot, "new-claude-rules");
        var step = new WriteBrainSkillsStep(claudeRulesDirectory: newDir);
        var ctx = BuildContext(AgentProviderType.ClaudeCode, "- [2+] For X, do Y (#1).");

        await step.ExecuteAsync(ctx, CancellationToken.None);

        File.Exists(Path.Combine(newDir, ClaudeSteeringFiles.BrainFileName)).Should().BeTrue();
    }

    [Fact]
    public async Task ClaudeCode_WithNoBrainSkills_RemovesStaleFile()
    {
        Directory.CreateDirectory(_claudeRulesDir);
        var brainFile = Path.Combine(_claudeRulesDir, ClaudeSteeringFiles.BrainFileName);
        File.WriteAllText(brainFile, "stale");

        var step = new WriteBrainSkillsStep(claudeRulesDirectory: _claudeRulesDir);
        var ctx = BuildContext(AgentProviderType.ClaudeCode, brainSkillsText: null);

        await step.ExecuteAsync(ctx, CancellationToken.None);

        File.Exists(brainFile).Should().BeFalse();
    }

    // ── OpenCode ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenCode_WithBrainSkills_WritesFileAndInstructions()
    {
        var step = new WriteBrainSkillsStep(openCodeDirectory: _openCodeDir);
        var ctx = BuildContext(AgentProviderType.OpenCode, "- [2+] For X, do Y (#1).");

        await step.ExecuteAsync(ctx, CancellationToken.None);

        var brainFile = Path.Combine(_openCodeDir, OpenCodeSteeringFiles.BrainFileName);
        File.Exists(brainFile).Should().BeTrue();
        File.ReadAllText(brainFile).Should().Contain("- [2+] For X, do Y (#1).");
        File.ReadAllText(Path.Combine(_openCodeDir, OpenCodeSteeringFiles.ConfigFileName)).Should().Contain("pipeline-*.md");
    }

    [Fact]
    public async Task OpenCode_InstructionsEntryAppearsOnce_AfterTwoCalls()
    {
        var step = new WriteBrainSkillsStep(openCodeDirectory: _openCodeDir);
        var ctx = BuildContext(AgentProviderType.OpenCode, "- [2+] For X, do Y (#1).");

        await step.ExecuteAsync(ctx, CancellationToken.None);
        await step.ExecuteAsync(ctx, CancellationToken.None);

        var configContent = File.ReadAllText(Path.Combine(_openCodeDir, OpenCodeSteeringFiles.ConfigFileName));
        configContent.Split("pipeline-*.md").Length.Should().Be(2, "entry should appear exactly once");
    }

    // ── Callbacks implementation ──────────────────────────────────────────────

    private sealed class NoOpCallbacks : IPipelineCallbacks
    {
        public void TransitionTo(PipelineStep step) { }
        public void EmitOutputLine(string line) { }
        public void NotifyChange() { }
        public Task AddRunToHistoryAsync(PipelineRun r) => Task.CompletedTask;
        public Task UpdateFileChangeStats(PipelineRun r) => Task.CompletedTask;
        public Task SwapAgentLabel(IssueIdentifier id, string label, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveAllAgentLabels(IssueIdentifier id, CancellationToken ct) => Task.CompletedTask;
        public Task CreatePullRequest(PipelineRun r, bool isDraft, CancellationToken ct) => Task.CompletedTask;
        public Task CreateDraftPrIfNotExists(PipelineRun r, CancellationToken ct) => Task.CompletedTask;
        public Task FinalizePullRequest(PipelineRun r, bool isDraft, CancellationToken ct) => Task.CompletedTask;
        public Task ReportBrainSyncResult(bool contextLoaded, int knowledgeFileCount) => Task.CompletedTask;
        public Task ReportPipelineRunEvent(string eventType, string? data) => Task.CompletedTask;
    }
}
