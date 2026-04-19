using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using Serilog.Context;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Singleton service that coordinates the entire automated development pipeline.
/// Manages the active pipeline run, state transitions, chat interaction, retry logic,
/// quality gate validation, and PR creation.
/// </summary>
public class PipelineOrchestrationService : IDisposable
{
    private readonly IConfigurationStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly IssueDescriptionParser _issueParser;
    private readonly IQualityGateValidator _qualityGateValidator;
    private readonly Serilog.ILogger _logger;
    private readonly List<PipelineRunSummary> _runHistory = new();
    private readonly string _runsDirectory;

    /// <summary>Marker text used to identify analysis comments on GitHub issues.</summary>
    private const string AnalysisCommentMarker = "## 🤖 Agent Analysis";

    private CancellationTokenSource? _cancellationTokenSource;
    private IAgentProvider? _activeAgentProvider;
    private IRepositoryProvider? _activeRepoProvider;
    private IIssueProvider? _activeIssueProvider;
    private IPipelineProvider? _activePipelineProvider;
    private PipelineConfiguration? _activeConfig;
    private string _activeBaseBranch = "main";

    // Stored between analysis and implementation phases
    private IssueDetail? _activeIssue;
    private ParsedIssue? _activeParsedIssue;
    private IReadOnlyList<IssueComment>? _activeIssueComments;

    /// <summary>Fired after each state transition for UI binding.</summary>
    public event Action? OnChange;

    /// <summary>Fired for each agent output line for real-time display.</summary>
    public event Action<string>? OnOutputLine;

    /// <summary>The currently active pipeline run, or null if idle.</summary>
    public PipelineRun? ActiveRun { get; private set; }

    /// <summary>Whether a pipeline run is currently in progress.</summary>
    public bool IsRunning => ActiveRun != null
        && ActiveRun.CurrentStep != PipelineStep.Completed
        && ActiveRun.CurrentStep != PipelineStep.Failed
        && ActiveRun.CurrentStep != PipelineStep.Cancelled;

    public PipelineOrchestrationService(
        IConfigurationStore configStore,
        IProviderFactory providerFactory,
        IssueDescriptionParser issueParser,
        IQualityGateValidator qualityGateValidator,
        Serilog.ILogger logger,
        string runsDirectory = "config/pipeline/runs")
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(issueParser);
        ArgumentNullException.ThrowIfNull(qualityGateValidator);
        ArgumentNullException.ThrowIfNull(logger);

        _configStore = configStore;
        _providerFactory = providerFactory;
        _issueParser = issueParser;
        _qualityGateValidator = qualityGateValidator;
        _logger = logger;
        _runsDirectory = runsDirectory;

        // Load persisted run history
        LoadRunHistory();
    }

    /// <summary>
    /// Starts a new pipeline run for the given issue and repository providers.
    /// Rejects if a pipeline is already running.
    /// </summary>
    public async Task<PipelineRun> StartPipelineAsync(
        string issueProviderId, string repoProviderId, string issueIdentifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueProviderId);
        ArgumentNullException.ThrowIfNull(repoProviderId);
        ArgumentNullException.ThrowIfNull(issueIdentifier);

        if (IsRunning)
            throw new InvalidOperationException("A pipeline run is already in progress.");

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = _cancellationTokenSource.Token;

        try
        {
            // Load pipeline configuration
            _activeConfig = await _configStore.LoadPipelineConfigAsync(linkedCt);

            // Resolve provider configs
            var issueProviderConfig = await ResolveProviderConfigAsync(issueProviderId, ProviderKind.Issue, linkedCt);
            var repoProviderConfig = await ResolveProviderConfigAsync(repoProviderId, ProviderKind.Repository, linkedCt);

            // Resolve agent provider (first configured agent provider)
            var agentConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Agent, linkedCt);
            if (agentConfigs.Count == 0)
                throw new InvalidOperationException("No agent provider configured. Add one in Settings.");
            var agentProviderConfig = agentConfigs[0];

            // Create provider instances
            var issueProvider = _providerFactory.CreateIssueProvider(issueProviderConfig);
            _activeIssueProvider = issueProvider;
            _activeRepoProvider = _providerFactory.CreateRepositoryProvider(repoProviderConfig);
            _activeAgentProvider = _providerFactory.CreateAgentProvider(agentProviderConfig);
            _activeBaseBranch = repoProviderConfig.Settings.GetValueOrDefault("baseBranch", "main");

            // Create the pipeline run
            var run = new PipelineRun
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = issueIdentifier,
                IssueTitle = string.Empty, // Will be set after fetching issue
                IssueProviderConfigId = issueProviderId,
                RepoProviderConfigId = repoProviderId,
                StartedAt = DateTime.UtcNow,
                CurrentStep = PipelineStep.Created,
                RepositoryName = $"{repoProviderConfig.Settings.GetValueOrDefault("owner", "")}/{repoProviderConfig.Settings.GetValueOrDefault("repo", "")}"
            };
            ActiveRun = run;

            // Create pipeline provider if external CI is enabled
            _activePipelineProvider = null;
            if (_activeConfig.ExternalCiEnabled)
            {
                var pipelineConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Pipeline, linkedCt);
                if (pipelineConfigs.Count > 0)
                {
                    _activePipelineProvider = _providerFactory.CreatePipelineProvider(pipelineConfigs[0]);
                    _logger.Information("Pipeline {RunId} external CI provider configured", run.RunId);
                }
                else
                {
                    _logger.Warning("Pipeline {RunId} external CI enabled but no pipeline provider configured", run.RunId);
                }
            }

            _logger.Information("Pipeline {RunId} created for issue {IssueIdentifier}", run.RunId, issueIdentifier);
            NotifyChange();

            // Execute the pipeline steps (fire-and-forget within the lock scope,
            // but we await to completion before returning)
            await ExecutePipelineStepsAsync(run, issueProvider, linkedCt);

            return run;
        }
        catch (Exception ex) when (ex is not InvalidOperationException || ActiveRun != null)
        {
            if (ActiveRun != null && ActiveRun.CurrentStep != PipelineStep.Failed
                && ActiveRun.CurrentStep != PipelineStep.Cancelled)
            {
                await HandlePipelineErrorAsync(ActiveRun, ex);
            }
            throw;
        }
    }

    private async Task ExecutePipelineStepsAsync(
        PipelineRun run, IIssueProvider issueProvider, CancellationToken ct)
    {
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);

        try
        {
            // Fetch and validate issue
            IssueDetail issue;
            try
            {
                issue = await issueProvider.GetIssueAsync(run.IssueIdentifier, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, "Pipeline {RunId} failed to fetch issue {IssueIdentifier}",
                    run.RunId, run.IssueIdentifier);
                run.FailureReason = $"Failed to fetch issue: {ex.Message}";
                TransitionTo(run, PipelineStep.Failed);
                AddRunToHistory(run);
                return;
            }

            // Validate issue has required fields
            if (string.IsNullOrWhiteSpace(issue.Title) || string.IsNullOrWhiteSpace(issue.Description))
            {
                _logger.Warning("Pipeline {RunId} issue has insufficient information", run.RunId);
                run.FailureReason = "insufficient issue information";
                TransitionTo(run, PipelineStep.Failed);
                AddRunToHistory(run);
                return;
            }

            // Update run with issue title
            run.IssueTitle = issue.Title;
            run.IssueLabels = issue.Labels;

            // Parse issue description
            var parsed = _issueParser.Parse(issue.Description);

            // Store for use after approval
            _activeIssue = issue;
            _activeParsedIssue = parsed;

            // Fetch issue comments for additional context
            IReadOnlyList<IssueComment> issueComments = Array.Empty<IssueComment>();
            try
            {
                issueComments = await issueProvider.ListCommentsAsync(run.IssueIdentifier, ct);
                _logger.Information("Pipeline {RunId} fetched {CommentCount} comment(s) for issue {IssueIdentifier}",
                    run.RunId, issueComments.Count, run.IssueIdentifier);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Pipeline {RunId} failed to fetch issue comments, proceeding without them",
                    run.RunId);
            }
            _activeIssueComments = issueComments;

            // Create workspace
            var workspacePath = Path.Combine(_activeConfig!.WorkspaceBaseDirectory, run.RunId);
            Directory.CreateDirectory(workspacePath);
            run.WorkspacePath = workspacePath;
            _logger.Information("Pipeline {RunId} workspace created at {WorkspacePath}", run.RunId, workspacePath);

            // Clone repository
            TransitionTo(run, PipelineStep.CloningRepository);
            try
            {
                await _activeRepoProvider!.CloneAsync(workspacePath, ct);
                _logger.Information("Pipeline {RunId} repository cloned successfully", run.RunId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, "Pipeline {RunId} failed to clone repository", run.RunId);
                run.FailureReason = $"Repository clone failed: {ex.Message}";
                TransitionTo(run, PipelineStep.Failed);
                AddRunToHistory(run);
                return;
            }

            // Create branch
            TransitionTo(run, PipelineStep.CreatingBranch);
            try
            {
                var branchName = PipelineFormatting.GenerateBranchName(
                    run.IssueIdentifier, issue.Title, run.RunId);
                var createdBranch = await _activeRepoProvider.CreateBranchAsync(
                    workspacePath, branchName, ct);
                run.BranchName = createdBranch;
                _logger.Information("Pipeline {RunId} branch {BranchName} created", run.RunId, createdBranch);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, "Pipeline {RunId} failed to create branch", run.RunId);
                run.FailureReason = $"Branch creation failed: {ex.Message}";
                TransitionTo(run, PipelineStep.Failed);
                AddRunToHistory(run);
                return;
            }

            // Check for existing analysis comment on the issue
            string? existingAnalysis = null;
            try
            {
                var comments = await issueProvider.ListCommentsAsync(run.IssueIdentifier, ct);
                var analysisComment = comments.FirstOrDefault(c => c.Body.Contains(AnalysisCommentMarker));
                if (analysisComment != null)
                {
                    existingAnalysis = analysisComment.Body;
                    _logger.Information("Pipeline {RunId} found existing analysis comment on issue {IssueIdentifier}, skipping agent analysis",
                        run.RunId, run.IssueIdentifier);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Pipeline {RunId} failed to check for existing analysis comments, will run fresh analysis",
                    run.RunId);
            }

            if (existingAnalysis != null)
            {
                // Reuse existing analysis — skip agent analysis and comment posting
                run.AnalysisContent = existingAnalysis;
                run.AnalysisSkipped = true;
                TransitionTo(run, PipelineStep.AnalyzingCode);

                // Send a read-only warm-up prompt to establish a session.
                // The first --no-interactive call can't write; --resume on a subsequent call can.
                try
                {
                    var warmupRequest = new AgentRequest
                    {
                        Prompt = "Briefly describe the project structure of this workspace. Do not make any changes.",
                        WorkspacePath = workspacePath,
                        Timeout = _activeConfig.AgentTimeout
                    };
                    await _activeAgentProvider!.ExecuteAsync(warmupRequest, ct,
                        line =>
                        {
                            var clean = StripAnsi(line);
                            run.OutputLines.Enqueue(clean);
                            OnOutputLine?.Invoke(clean);
                        });
                    _logger.Information("Pipeline {RunId} session established via warm-up prompt", run.RunId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Pipeline {RunId} warm-up prompt failed", run.RunId);
                }

                TransitionTo(run, PipelineStep.PostingAnalysis);
            }
            else
            {
                // Analyze code — agent examines the codebase in context of the issue
                TransitionTo(run, PipelineStep.AnalyzingCode);

                // Send a read-only warm-up prompt to establish a session.
                // The first --no-interactive call can't write; --resume on a subsequent call can.
                try
                {
                    var warmupRequest = new AgentRequest
                    {
                        Prompt = "Briefly describe the project structure of this workspace. Do not make any changes.",
                        WorkspacePath = workspacePath,
                        Timeout = _activeConfig.AgentTimeout
                    };
                    await _activeAgentProvider!.ExecuteAsync(warmupRequest, ct,
                        line =>
                        {
                            var clean = StripAnsi(line);
                            run.OutputLines.Enqueue(clean);
                            OnOutputLine?.Invoke(clean);
                        });
                    _logger.Information("Pipeline {RunId} session established via warm-up prompt", run.RunId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Pipeline {RunId} warm-up prompt failed", run.RunId);
                }

                try
                {
                    var analysisPrompt = PromptBuilder.BuildAnalysisPrompt(issue, parsed, issueComments);
                    var analysisRequest = new AgentRequest
                    {
                        Prompt = analysisPrompt,
                        WorkspacePath = workspacePath,
                        Timeout = _activeConfig.AgentTimeout
                    };

                    await _activeAgentProvider!.ExecuteWithResumeAsync(
                        analysisRequest.Prompt,
                        analysisRequest.WorkspacePath,
                        analysisRequest.Timeout,
                        ct,
                        line =>
                        {
                            var clean = StripAnsi(line);
                            run.OutputLines.Enqueue(clean);
                            OnOutputLine?.Invoke(clean);
                        });

                    // Read the analysis from the file the agent wrote
                    var analysisFilePath = Path.Combine(workspacePath, PromptBuilder.AnalysisFilePath);
                    if (File.Exists(analysisFilePath))
                    {
                        run.AnalysisContent = await File.ReadAllTextAsync(analysisFilePath, ct);
                        _logger.Information("Pipeline {RunId} read analysis from {AnalysisFilePath}",
                            run.RunId, analysisFilePath);
                    }
                    else
                    {
                        _logger.Warning("Pipeline {RunId} agent did not write analysis file at {AnalysisFilePath}",
                            run.RunId, analysisFilePath);
                        run.AnalysisContent = null;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Pipeline {RunId} code analysis failed, falling back to issue-based analysis",
                        run.RunId);
                    run.AnalysisContent = null;
                }

                // Post analysis comment on the issue (non-fatal)
                TransitionTo(run, PipelineStep.PostingAnalysis);
                try
                {
                    var analysis = !string.IsNullOrWhiteSpace(run.AnalysisContent)
                        ? IssueAnalysisComment.FromAgentAnalysis(issue, run.AnalysisContent)
                        : IssueAnalysisComment.FromIssue(issue, parsed);

                    await _activeIssueProvider!.PostCommentAsync(run.IssueIdentifier, analysis.ToMarkdown(), ct);
                    _logger.Information("Pipeline {RunId} posted analysis comment on issue {IssueIdentifier}",
                        run.RunId, run.IssueIdentifier);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Pipeline {RunId} failed to post analysis comment on issue {IssueIdentifier}",
                        run.RunId, run.IssueIdentifier);
                }
            }

            // Pause for user approval before implementation
            TransitionTo(run, PipelineStep.WaitingForAnalysisApproval);
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Pipeline {RunId} was cancelled", run.RunId);
            run.CompletedAt = DateTime.UtcNow;
            TransitionTo(run, PipelineStep.Cancelled);
            AddRunToHistory(run);
        }
    }

    /// <summary>
    /// Approves the analysis and continues the pipeline to code generation.
    /// Must be called when the pipeline is in WaitingForAnalysisApproval state.
    /// </summary>
    public async Task ApproveAnalysisAsync(CancellationToken ct)
    {
        if (ActiveRun == null || ActiveRun.CurrentStep != PipelineStep.WaitingForAnalysisApproval)
            throw new InvalidOperationException("No active pipeline run in WaitingForAnalysisApproval state.");

        var run = ActiveRun;
        run.ApprovalTimestamp = DateTime.UtcNow;
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);

        using var linkedCts = _cancellationTokenSource != null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, _cancellationTokenSource.Token)
            : null;
        var linkedCt = linkedCts?.Token ?? ct;

        try
        {
            // Generate code — agent implements the issue
            TransitionTo(run, PipelineStep.GeneratingCode);
            try
            {
                var prompt = PromptBuilder.BuildPrompt(_activeIssue!, _activeParsedIssue!, _activeIssueComments);

                var lastOutputTime = DateTime.UtcNow;
                var stallWarningInterval = TimeSpan.FromMinutes(5);

                // Stall monitor: periodically check if the agent is still producing output
                var stallCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCt);
                var stallMonitorTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!stallCts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), stallCts.Token);
                            var silence = DateTime.UtcNow - lastOutputTime;
                            var elapsed = DateTime.UtcNow - run.StartedAt;
                            if (silence >= stallWarningInterval)
                            {
                                var msg = $"No agent output for {silence.TotalMinutes:F0}m. " +
                                          $"Agent call still in progress. " +
                                          $"Total elapsed: {elapsed:hh\\:mm\\:ss}. Timeout: {_activeConfig!.AgentTimeout:hh\\:mm\\:ss}.";
                                _logger.Warning("Pipeline {RunId} {StallMessage}", run.RunId, msg);
                                run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = msg });
                                NotifyChange();
                                // Reset so we warn again after another interval
                                lastOutputTime = DateTime.UtcNow;
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                }, CancellationToken.None);

                var agentResult = await _activeAgentProvider!.ExecuteWithResumeAsync(
                    prompt,
                    run.WorkspacePath!,
                    _activeConfig!.AgentTimeout,
                    linkedCt,
                    line =>
                    {
                        lastOutputTime = DateTime.UtcNow;
                        var clean = StripAnsi(line);
                        run.OutputLines.Enqueue(clean);
                        OnOutputLine?.Invoke(clean);
                    });

                // Stop stall monitor
                await stallCts.CancelAsync();
                try { await stallMonitorTask; } catch (OperationCanceledException) { }
                stallCts.Dispose();

                var outputSummary = agentResult.OutputLines.Count > 0
                    ? StripAnsi(string.Join(Environment.NewLine, agentResult.OutputLines.TakeLast(10)))
                    : "(no output)";

                run.ChatHistory.Enqueue(new ChatEntry
                {
                    Role = ChatRole.Agent,
                    Content = outputSummary
                });

                _logger.Information("Pipeline {RunId} initial code generation completed with exit code {ExitCode} after {Elapsed}",
                    run.RunId, agentResult.ExitCode, DateTime.UtcNow - run.StartedAt);

                UpdateFileChangeStats(run);

                if (agentResult.ExitCode != 0)
                {
                    _logger.Warning("Pipeline {RunId} agent exited with non-zero code {ExitCode}, continuing to chat phase",
                        run.RunId, agentResult.ExitCode);
                    run.ChatHistory.Enqueue(new ChatEntry
                    {
                        Role = ChatRole.System,
                        Content = $"Agent process exited with code {agentResult.ExitCode} after {(DateTime.UtcNow - run.StartedAt):hh\\:mm\\:ss}. " +
                                  $"Output lines captured: {agentResult.OutputLines.Count}. " +
                                  $"The process may have stopped unexpectedly. You can continue via chat."
                    });
                }
            }
            catch (OperationCanceledException) when (_cancellationTokenSource?.IsCancellationRequested == true)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Agent timeout
                _logger.Warning("Pipeline {RunId} agent timed out after {Duration}",
                    run.RunId, _activeConfig!.AgentTimeout);
                run.ChatHistory.Enqueue(new ChatEntry
                {
                    Role = ChatRole.System,
                    Content = $"Agent timed out after {_activeConfig.AgentTimeout}"
                });
            }
            catch (Exception ex)
            {
                // Agent crash or unexpected error — log and continue to WaitingForChat
                // so the user can retry via chat instead of the pipeline dying
                _logger.Warning(ex, "Pipeline {RunId} code generation failed, continuing to chat phase", run.RunId);
                run.ChatHistory.Enqueue(new ChatEntry
                {
                    Role = ChatRole.System,
                    Content = $"Agent process failed: {ex.Message}. You can retry via chat."
                });
            }

            // Self-review step (if enabled)
            if (_activeConfig!.SelfReviewEnabled && _activeConfig.SelfReviewMaxIterations > 0)
            {
                run.ReviewIterationsTotal = _activeConfig.SelfReviewMaxIterations;
                for (var i = 0; i < _activeConfig.SelfReviewMaxIterations; i++)
                {
                    run.ReviewIterationInProgress = i + 1;
                    TransitionTo(run, PipelineStep.ReviewingCode);
                    _logger.Information(
                        "Pipeline {RunId} starting self-review iteration {Iteration}/{MaxIterations}",
                        run.RunId, i + 1, _activeConfig.SelfReviewMaxIterations);

                    run.ChatHistory.Enqueue(new ChatEntry
                    {
                        Role = ChatRole.System,
                        Content = $"Self-review iteration {i + 1}/{_activeConfig.SelfReviewMaxIterations} starting..."
                    });
                    NotifyChange();

                    try
                    {
                        var reviewResult = await _activeAgentProvider!.ExecuteWithResumeAsync(
                            _activeConfig.SelfReviewPrompt,
                            run.WorkspacePath!,
                            _activeConfig.AgentTimeout,
                            linkedCt,
                            line =>
                            {
                                var clean = StripAnsi(line);
                                run.OutputLines.Enqueue(clean);
                                OnOutputLine?.Invoke(clean);
                            });

                        run.ReviewIterationsCompleted++;

                        var reviewOutput = reviewResult.OutputLines.Count > 0
                            ? StripAnsi(string.Join(Environment.NewLine, reviewResult.OutputLines.TakeLast(10)))
                            : "(no output)";

                        _logger.Information(
                            "Pipeline {RunId} self-review iteration {Iteration} completed with exit code {ExitCode}. Review output: {ReviewOutput}",
                            run.RunId, i + 1, reviewResult.ExitCode, reviewOutput);

                        run.ChatHistory.Enqueue(new ChatEntry
                        {
                            Role = ChatRole.Agent,
                            Content = $"[Self-review {i + 1}/{_activeConfig.SelfReviewMaxIterations}] {reviewOutput}"
                        });
                        NotifyChange();
                    }
                    catch (OperationCanceledException) when (_cancellationTokenSource?.IsCancellationRequested == true)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex,
                            "Pipeline {RunId} self-review iteration {Iteration} failed, skipping remaining reviews",
                            run.RunId, i + 1);
                        run.ChatHistory.Enqueue(new ChatEntry
                        {
                            Role = ChatRole.System,
                            Content = $"Self-review iteration {i + 1} failed: {ex.Message}"
                        });
                        NotifyChange();
                        break;
                    }
                }

                run.ReviewIterationInProgress = 0;
            }

            // Transition to WaitingForChat
            TransitionTo(run, PipelineStep.WaitingForChat);
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Pipeline {RunId} was cancelled", run.RunId);
            run.CompletedAt = DateTime.UtcNow;
            TransitionTo(run, PipelineStep.Cancelled);
            AddRunToHistory(run);
        }
    }

    /// <summary>
    /// Sends a chat message to the agent during WaitingForChat state.
    /// </summary>
    public async Task SendChatMessageAsync(string message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (ActiveRun == null || ActiveRun.CurrentStep != PipelineStep.WaitingForChat)
            throw new InvalidOperationException("No active pipeline run in WaitingForChat state.");

        var run = ActiveRun;
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);

        // Add user message to chat history
        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.User, Content = message });
        NotifyChange();

        // Transition to GeneratingCode
        TransitionTo(run, PipelineStep.GeneratingCode);

        try
        {
            using var linkedCts = _cancellationTokenSource != null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct, _cancellationTokenSource.Token)
                : null;
            var linkedCt = linkedCts?.Token ?? ct;

            var agentResult = await _activeAgentProvider!.ExecuteWithResumeAsync(
                message, run.WorkspacePath!, _activeConfig!.AgentTimeout, linkedCt,
                line =>
                {
                    var clean = StripAnsi(line);
                    run.OutputLines.Enqueue(clean);
                    OnOutputLine?.Invoke(clean);
                });

            var outputSummary = agentResult.OutputLines.Count > 0
                ? StripAnsi(string.Join(Environment.NewLine, agentResult.OutputLines.TakeLast(10)))
                : "(no output)";

            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.Agent,
                Content = outputSummary
            });

            _logger.Information("Pipeline {RunId} chat response completed with exit code {ExitCode}",
                run.RunId, agentResult.ExitCode);

            UpdateFileChangeStats(run);
        }
        catch (OperationCanceledException) when (_cancellationTokenSource?.IsCancellationRequested == true)
        {
            _logger.Information("Pipeline {RunId} was cancelled during chat", run.RunId);
            run.CompletedAt = DateTime.UtcNow;
            TransitionTo(run, PipelineStep.Cancelled);
            AddRunToHistory(run);
            return;
        }
        catch (OperationCanceledException)
        {
            // Agent timeout during chat
            _logger.Warning("Pipeline {RunId} agent timed out during chat after {Duration}",
                run.RunId, _activeConfig!.AgentTimeout);
            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = $"Agent timed out after {_activeConfig.AgentTimeout}"
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Pipeline {RunId} chat execution failed", run.RunId);
            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = $"Agent error: {ex.Message}"
            });
        }

        // Return to WaitingForChat
        TransitionTo(run, PipelineStep.WaitingForChat);
    }

    /// <summary>
    /// Proceeds from WaitingForChat to quality gate validation.
    /// Handles pass/fail/retry logic including draft PR on exhausted retries.
    /// </summary>
    public async Task ProceedToQualityGatesAsync(CancellationToken ct)
    {
        if (ActiveRun == null || ActiveRun.CurrentStep != PipelineStep.WaitingForChat)
            throw new InvalidOperationException("No active pipeline run in WaitingForChat state.");

        var run = ActiveRun;
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);

        TransitionTo(run, PipelineStep.RunningQualityGates);

        try
        {
            using var linkedCts = _cancellationTokenSource != null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct, _cancellationTokenSource.Token)
                : null;
            var linkedCt = linkedCts?.Token ?? ct;

            // Run local quality gates first (compilation, tests, coverage)
            var report = await _qualityGateValidator.ValidateAsync(
                run.WorkspacePath!, _activeConfig!, linkedCt);

            // If local gates passed and external CI is enabled, push and wait for CI
            if (report.Compilation.Passed && report.Tests.Passed
                && (report.Coverage?.Passed ?? true) && (report.SecurityScan?.Passed ?? true)
                && _activeConfig!.ExternalCiEnabled && _activePipelineProvider != null)
            {
                GateResult? externalCiGate = null;
                try
                {
                    var commitMessage = PipelineFormatting.GenerateCommitMessage(
                        run.IssueTitle, run.IssueIdentifier);
                    await _activeRepoProvider!.CommitAllAsync(run.WorkspacePath!, commitMessage, linkedCt);
                    await _activeRepoProvider.PushBranchAsync(run.WorkspacePath!, run.BranchName!, linkedCt);
                    _logger.Information("Pipeline {RunId} pushed branch {BranchName} for CI validation",
                        run.RunId, run.BranchName);

                    // Get HEAD commit SHA for precise CI matching
                    string? commitSha = null;
                    try
                    {
                        var gitLogResult = await RunGitCommandAsync(run.WorkspacePath!, "rev-parse HEAD", linkedCt);
                        commitSha = gitLogResult?.Trim();
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Pipeline {RunId} could not read HEAD commit SHA", run.RunId);
                    }

                    // Wait for external CI — logs for failed jobs are fetched
                    // automatically by the provider before returning
                    var ciStatus = await _activePipelineProvider.WaitForCompletionAsync(
                        run.BranchName!, commitSha, _activeConfig.ExternalCiTimeout, linkedCt);

                    var ciPassed = ciStatus.State == PipelineRunState.Passed;

                    // Write full CI logs to .kiro/ci-logs/ so the agent can read them on demand
                    if (!ciPassed && run.WorkspacePath != null)
                    {
                        WriteCiLogsToWorkspace(ciStatus, run.WorkspacePath, run.RunId);
                    }

                    externalCiGate = new GateResult
                    {
                        GateName = "External CI",
                        Passed = ciPassed,
                        Details = ciPassed
                            ? $"CI passed. {ciStatus.Jobs.Count} job(s) completed."
                            : QualityGateValidator.BuildCiFailureDetails(ciStatus)
                    };
                }
                catch (OperationCanceledException) when (!linkedCt.IsCancellationRequested)
                {
                    externalCiGate = new GateResult
                    {
                        GateName = "External CI",
                        Passed = false,
                        Details = $"External CI timed out after {_activeConfig.ExternalCiTimeout}"
                    };
                }
                catch (OperationCanceledException)
                {
                    throw; // Pipeline cancellation — propagate
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Pipeline {RunId} external CI check failed, treating as gate failure", run.RunId);
                    externalCiGate = new GateResult
                    {
                        GateName = "External CI",
                        Passed = false,
                        Details = $"External CI error: {ex.Message}"
                    };
                }

                // Rebuild report with external CI result
                report = new QualityGateReport
                {
                    Compilation = report.Compilation,
                    Tests = report.Tests,
                    Coverage = report.Coverage,
                    SecurityScan = report.SecurityScan,
                    ExternalCi = externalCiGate
                };
            }

            run.LatestQualityReport = report;
            run.QualityGateHistory.Enqueue(report);

            _logger.Information(
                "Pipeline {RunId} quality gates: AllPassed={AllPassed}, Compilation={CompilationPassed}, Tests={TestsPassed}",
                run.RunId, report.AllPassed, report.Compilation.Passed, report.Tests.Passed);

            if (report.AllPassed)
            {
                // All gates passed — create PR
                await CreatePullRequestAsync(run, report, isDraft: false, linkedCt);
            }
            else if (run.RetryCount < _activeConfig!.MaxRetries)
            {
                // Gates failed, retries left — auto-fix via agent
                run.RetryCount++;
                var errorSummary = BuildQualityGateErrorSummary(report);
                run.RetryErrors.Add(errorSummary);

                _logger.Information(
                    "Pipeline {RunId} quality gates failed, auto-retry {RetryCount}/{MaxRetries}",
                    run.RunId, run.RetryCount, _activeConfig.MaxRetries);

                run.ChatHistory.Enqueue(new ChatEntry
                {
                    Role = ChatRole.System,
                    Content = $"Quality gates failed (attempt {run.RetryCount}/{_activeConfig.MaxRetries}):\n{errorSummary}"
                });

                // Transition to WaitingForChat so SendChatMessageAsync's precondition is met
                TransitionTo(run, PipelineStep.WaitingForChat);

                // Automatically send the failure details to the agent to fix
                var fixPrompt = $"The quality gates failed. Please fix the following issues:\n{errorSummary}";
                await SendChatMessageAsync(fixPrompt, linkedCt);

                // Agent has attempted the fix — re-run quality gates
                await ProceedToQualityGatesAsync(ct);
            }
            else
            {
                // Max retries exhausted — create draft PR
                _logger.Warning(
                    "Pipeline {RunId} max retries ({MaxRetries}) exhausted, creating draft PR",
                    run.RunId, _activeConfig!.MaxRetries);

                var errorSummary = BuildQualityGateErrorSummary(report);
                run.RetryErrors.Add(errorSummary);

                await CreatePullRequestAsync(run, report, isDraft: true, linkedCt);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Pipeline {RunId} was cancelled during quality gates", run.RunId);
            run.CompletedAt = DateTime.UtcNow;
            TransitionTo(run, PipelineStep.Cancelled);
            AddRunToHistory(run);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Pipeline {RunId} quality gate validation failed", run.RunId);
            run.FailureReason = $"Quality gate validation error: {ex.Message}";
            TransitionTo(run, PipelineStep.Failed);
            AddRunToHistory(run);
        }
    }

    /// <summary>
    /// Cancels the active pipeline run.
    /// </summary>
    public Task CancelPipelineAsync()
    {
        if (ActiveRun == null || !IsRunning)
            return Task.CompletedTask;

        var run = ActiveRun;
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);

        _logger.Information("Pipeline {RunId} cancellation requested", run.RunId);

        _cancellationTokenSource?.Cancel();
        run.CompletedAt = DateTime.UtcNow;
        TransitionTo(run, PipelineStep.Cancelled);
        AddRunToHistory(run);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the in-memory run history.
    /// </summary>
    public IReadOnlyList<PipelineRunSummary> GetRunHistory() => _runHistory.AsReadOnly();

    // --- Private helpers ---

    private async Task CreatePullRequestAsync(
        PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct)
    {
        TransitionTo(run, PipelineStep.CreatingPullRequest);

        try
        {
            // Commit any uncommitted changes and push (skip if already pushed by external CI step)
            try
            {
                var commitMessage = PipelineFormatting.GenerateCommitMessage(
                    run.IssueTitle, run.IssueIdentifier);
                await _activeRepoProvider!.CommitAllAsync(run.WorkspacePath!, commitMessage, ct);
                await _activeRepoProvider!.PushBranchAsync(run.WorkspacePath!, run.BranchName!, ct);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("No changes to commit"))
            {
                _logger.Information(
                    "Pipeline {RunId} no uncommitted changes (already pushed during CI validation), skipping commit",
                    run.RunId);
            }

            // Verify the branch actually has commits beyond the base branch.
            // If the agent didn't implement anything, there's nothing to PR.
            if (!BranchHasCommitsAhead(run.WorkspacePath!, _activeBaseBranch))
            {
                _logger.Warning("Pipeline {RunId} branch has no commits ahead of {BaseBranch} — agent did not produce any changes",
                    run.RunId, _activeBaseBranch);
                run.FailureReason = "Agent did not produce any changes. No commits ahead of base branch.";
                run.CompletedAt = DateTime.UtcNow;
                TransitionTo(run, PipelineStep.Failed);
                AddRunToHistory(run);
                return;
            }

            // Build PR info
            var testsPassed = report.Tests.TestsPassed ?? 0;
            var testsFailed = report.Tests.TestsFailed ?? 0;
            var testsSkipped = report.Tests.TestsSkipped ?? 0;
            var coverage = report.Coverage?.CoveragePercent;

            var fileChanges = PipelineFormatting.GetFileChanges(run.WorkspacePath!, _activeBaseBranch);

            var issueTitle = _activeIssue?.Title ?? run.IssueTitle;
            var issueDescription = _activeIssue?.Description ?? string.Empty;
            var acceptanceCriteria = _activeParsedIssue?.AcceptanceCriteria
                ?? (IReadOnlyList<string>)Array.Empty<string>();

            var prTitle = PipelineFormatting.GeneratePrTitle(run.IssueTitle, run.IssueIdentifier);
            var prBody = PipelineFormatting.GeneratePrBody(
                run.IssueIdentifier, testsPassed, testsFailed, testsSkipped,
                coverage, fileChanges, issueTitle, issueDescription,
                acceptanceCriteria, isDraft, _activeIssueComments);

            var prInfo = new PullRequestInfo
            {
                Title = prTitle,
                Body = prBody,
                BranchName = run.BranchName!,
                BaseBranch = _activeBaseBranch,
                IsDraft = isDraft
            };

            var prUrl = await _activeRepoProvider!.CreatePullRequestAsync(prInfo, ct);
            run.PullRequestUrl = prUrl;
            run.IsDraftPr = isDraft;
            run.PullRequestNumber = ExtractPrNumber(prUrl);
            run.CompletedAt = DateTime.UtcNow;

            var finalStep = isDraft ? PipelineStep.Failed : PipelineStep.Completed;
            if (isDraft)
                run.FailureReason = "Quality gates failed after max retries; draft PR created.";

            TransitionTo(run, finalStep);
            AddRunToHistory(run);

            var duration = run.CompletedAt.Value - run.StartedAt;
            _logger.Information(
                "Pipeline {RunId} {Outcome} in {Duration}. Retries: {RetryCount}. PR: {PullRequestUrl}",
                run.RunId, finalStep, duration, run.RetryCount, prUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, "Pipeline {RunId} failed to create pull request", run.RunId);
            run.FailureReason = $"PR creation failed: {ex.Message}";
            TransitionTo(run, PipelineStep.Failed);
            AddRunToHistory(run);
        }
    }

    private void UpdateFileChangeStats(PipelineRun run)
    {
        try
        {
            if (string.IsNullOrEmpty(run.WorkspacePath)) return;
            var changes = PipelineFormatting.GetFileChanges(run.WorkspacePath, _activeBaseBranch);
            run.FilesChangedCount = changes.Count;
            // Approximate line stats from change count (detailed stats would require git diff --numstat)
            _logger.Debug("Pipeline {RunId} file changes: {Count} files", run.RunId, changes.Count);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Pipeline {RunId} failed to compute file change stats", run.RunId);
        }
    }

    private static string? ExtractPrNumber(string? prUrl)
    {
        if (string.IsNullOrEmpty(prUrl)) return null;
        // GitHub PR URLs: https://github.com/owner/repo/pull/47
        var lastSlash = prUrl.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < prUrl.Length - 1)
        {
            var candidate = prUrl[(lastSlash + 1)..];
            if (int.TryParse(candidate, out _)) return candidate;
        }
        return null;
    }

    private void TransitionTo(PipelineRun run, PipelineStep step)
    {
        var previousStep = run.CurrentStep;
        run.CurrentStep = step;
        _logger.Information("Pipeline {RunId} transitioned from {PreviousStep} to {Step}",
            run.RunId, previousStep, step);
        NotifyChange();
    }

    private void NotifyChange()
    {
        try
        {
            OnChange?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "OnChange handler threw an exception");
        }
    }

    private async Task<ProviderConfig> ResolveProviderConfigAsync(
        string providerId, ProviderKind kind, CancellationToken ct)
    {
        var configs = await _configStore.LoadProviderConfigsAsync(kind, ct);
        var config = configs.FirstOrDefault(c => c.Id == providerId);
        if (config == null)
            throw new InvalidOperationException(
                $"Provider config '{providerId}' of kind '{kind}' not found.");
        return config;
    }

    private async Task HandlePipelineErrorAsync(PipelineRun run, Exception ex)
    {
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);
        _logger.Error(ex, "Pipeline {RunId} encountered an unhandled error at step {Step}",
            run.RunId, run.CurrentStep);
        run.FailureReason = ex.Message;
        run.CompletedAt = DateTime.UtcNow;
        TransitionTo(run, PipelineStep.Failed);
        AddRunToHistory(run);
    }

    private void AddRunToHistory(PipelineRun run)
    {
        var summary = new PipelineRunSummary
        {
            RunId = run.RunId,
            IssueIdentifier = run.IssueIdentifier,
            IssueTitle = run.IssueTitle,
            FinalStep = run.CurrentStep,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            RetryCount = run.RetryCount,
            PullRequestUrl = run.PullRequestUrl
        };
        _runHistory.Insert(0, summary);
        PersistRunSummary(summary);
    }

    private void PersistRunSummary(PipelineRunSummary summary)
    {
        try
        {
            if (!Directory.Exists(_runsDirectory))
                Directory.CreateDirectory(_runsDirectory);

            var path = Path.Combine(_runsDirectory, $"{summary.RunId}.json");
            var json = System.Text.Json.JsonSerializer.Serialize(summary, _jsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to persist run summary {RunId}", summary.RunId);
        }
    }

    private void LoadRunHistory()
    {
        try
        {
            if (!Directory.Exists(_runsDirectory))
                return;

            var summaries = new List<PipelineRunSummary>();
            foreach (var file in Directory.GetFiles(_runsDirectory, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var summary = System.Text.Json.JsonSerializer.Deserialize<PipelineRunSummary>(json, _jsonOptions);
                    if (summary != null)
                        summaries.Add(summary);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to load run summary from {File}", file);
                }
            }

            _runHistory.AddRange(summaries.OrderByDescending(s => s.StartedAt));

            _logger.Information("Loaded {Count} pipeline run(s) from history", _runHistory.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load run history");
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static string BuildQualityGateErrorSummary(QualityGateReport report)
    {
        var errors = new List<string>();
        if (!report.Compilation.Passed)
            errors.Add($"Compilation: {report.Compilation.Details}");
        if (!report.Tests.Passed)
            errors.Add($"Tests: {report.Tests.Details}");
        if (report.Coverage is { Passed: false })
            errors.Add($"Coverage: {report.Coverage.Details}");
        if (report.SecurityScan is { Passed: false })
            errors.Add($"Security: {report.SecurityScan.Details}");
        if (report.ExternalCi is { Passed: false })
            errors.Add($"External CI: {report.ExternalCi.Details}");
        return string.Join(Environment.NewLine, errors);
    }

    /// <summary>
    /// Writes full CI job logs to .kiro/ci-logs/{runId}/ in the workspace.
    /// Each failed job gets its own file. Sets <see cref="PipelineJobResult.LogFilePath"/>
    /// so <see cref="QualityGateValidator.BuildCiFailureDetails"/> can reference the paths.
    /// Clears <see cref="PipelineJobResult.LogContent"/> after writing to free memory.
    /// </summary>
    private void WriteCiLogsToWorkspace(PipelineRunStatus ciStatus, string workspacePath, string runId)
    {
        var failedJobs = ciStatus.Jobs.Where(j => j.State == PipelineRunState.Failed && !string.IsNullOrEmpty(j.LogContent)).ToList();
        if (failedJobs.Count == 0)
            return;

        var logsDir = Path.Combine(workspacePath, ".kiro", "ci-logs", runId);
        try
        {
            // Clean previous logs for this run (handles retries writing to the same runId directory)
            if (Directory.Exists(logsDir))
                Directory.Delete(logsDir, recursive: true);

            Directory.CreateDirectory(logsDir);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to prepare CI logs directory {LogsDir}", logsDir);
            return;
        }

        var written = 0;
        foreach (var job in failedJobs)
        {
            try
            {
                var safeJobName = string.Join("_", job.Name.Split(Path.GetInvalidFileNameChars()));
                if (string.IsNullOrWhiteSpace(safeJobName))
                    safeJobName = $"job_{job.JobId}";

                var fileName = $"{safeJobName}_{job.JobId}.log";
                var fullPath = Path.Combine(logsDir, fileName);

                File.WriteAllText(fullPath, job.LogContent);

                // Store the workspace-relative path so the agent prompt can reference it
                job.LogFilePath = Path.Combine(".kiro", "ci-logs", runId, fileName).Replace('\\', '/');
                job.LogContent = null; // Free the potentially large string — it's on disk now

                _logger.Debug("Wrote CI log for job '{JobName}' to {LogPath}", job.Name, job.LogFilePath);
                written++;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to write CI log for job '{JobName}'", job.Name);
            }
        }

        if (written > 0)
            _logger.Information("Wrote {Count} CI log file(s) to {LogsDir}", written, logsDir);
    }

    /// <summary>
    /// Checks whether the current branch has any commits ahead of the base branch.
    /// Returns false if the branch tip equals the merge base (no new commits).
    /// </summary>
    private static bool BranchHasCommitsAhead(string workspacePath, string baseBranch)
    {
        try
        {
            using var repo = new LibGit2Sharp.Repository(workspacePath);
            var head = repo.Head.Tip;
            var baseBranchRef = repo.Branches[$"origin/{baseBranch}"]
                ?? repo.Branches[baseBranch];
            if (baseBranchRef == null) return true; // Can't determine — assume there are changes
            var mergeBase = repo.ObjectDatabase.FindMergeBase(head, baseBranchRef.Tip);
            return mergeBase == null || mergeBase.Sha != head.Sha;
        }
        catch
        {
            return true; // On error, assume there are changes and let PR creation decide
        }
    }

    private static string StripAnsi(string input) => AnsiStripper.Strip(input);

    private static async Task<string?> RunGitCommandAsync(string workspacePath, string args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workspacePath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null) return null;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 ? output : null;
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Dispose();
    }
}
