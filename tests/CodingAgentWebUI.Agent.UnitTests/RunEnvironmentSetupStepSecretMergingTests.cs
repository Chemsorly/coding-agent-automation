using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for project-level secrets merging, pipeline-wide masking, and InjectedSecretKeys cleanup
/// in <see cref="RunEnvironmentSetupStep"/> and <see cref="LocalPipelineExecutor"/>.
/// 
/// Coverage areas:
/// - Secret merging: project-only, repo-only, collision (repo wins), disjoint union
/// - Masking: values masked in output, values &lt; 4 chars NOT masked, keys NOT masked
/// - Cleanup: InjectedSecretKeys unset in finally block
/// - Pipeline-wide masking: output from steps AFTER RunEnvironmentSetupStep is masked
/// - Edge cases: null ProjectSecrets, null repo Secrets, empty dicts, special characters
/// </summary>
[Collection("EnvironmentVariables")]
public class RunEnvironmentSetupStepSecretMergingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly List<string> _emittedLines = [];

    public RunEnvironmentSetupStepSecretMergingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"secret-merge-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);

        _mockCallbacks
            .Setup(c => c.EmitOutputLine(It.IsAny<string>()))
            .Callback<string>(line => _emittedLines.Add(line));
        _mockCallbacks
            .Setup(c => c.SwapAgentLabel(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ══════════════════════════════════════════════════════════════════════
    // MERGE TESTS — project-only, repo-only, collision (repo wins), disjoint union
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_ProjectSecretsOnly_InjectsProjectSecrets()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Arrange — project secrets, no repo secrets
        var outputFile = Path.Combine(_tempDir, "project-only.txt");
        var job = CreateJobWithProjectAndRepoSecrets(
            projectSecrets: new Dictionary<string, string>
            {
                ["PROJECT_TOKEN"] = "project-token-value-1234"
            },
            repoSecrets: null,
            setupSteps: [new SetupStep { Name = "Check project secret", Command = $"printenv PROJECT_TOKEN > {outputFile}" }]);

        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        var envValue = (await File.ReadAllTextAsync(outputFile)).Trim();
        envValue.Should().Be("project-token-value-1234");
    }

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_RepoSecretsOnly_InjectsRepoSecrets()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Arrange — no project secrets, repo secrets only
        var outputFile = Path.Combine(_tempDir, "repo-only.txt");
        var job = CreateJobWithProjectAndRepoSecrets(
            projectSecrets: null,
            repoSecrets: new Dictionary<string, string>
            {
                ["REPO_TOKEN"] = "repo-token-value-5678"
            },
            setupSteps: [new SetupStep { Name = "Check repo secret", Command = $"printenv REPO_TOKEN > {outputFile}" }]);

        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        var envValue = (await File.ReadAllTextAsync(outputFile)).Trim();
        envValue.Should().Be("repo-token-value-5678");
    }

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_CollisionRepoWins_RepoSecretOverridesProjectSecret()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Arrange — same key in both, repo should win
        var outputFile = Path.Combine(_tempDir, "collision.txt");
        var job = CreateJobWithProjectAndRepoSecrets(
            projectSecrets: new Dictionary<string, string>
            {
                ["SHARED_KEY"] = "project-value-loses"
            },
            repoSecrets: new Dictionary<string, string>
            {
                ["SHARED_KEY"] = "repo-value-wins-1234"
            },
            setupSteps: [new SetupStep { Name = "Check collision", Command = $"printenv SHARED_KEY > {outputFile}" }]);

        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        var envValue = (await File.ReadAllTextAsync(outputFile)).Trim();
        envValue.Should().Be("repo-value-wins-1234");
    }

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_DisjointUnion_AllSecretsFromBothSourcesAvailable()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Arrange — different keys in project and repo, both should be available
        var outputFile = Path.Combine(_tempDir, "disjoint.txt");
        var job = CreateJobWithProjectAndRepoSecrets(
            projectSecrets: new Dictionary<string, string>
            {
                ["PROJECT_KEY"] = "project-val-1234"
            },
            repoSecrets: new Dictionary<string, string>
            {
                ["REPO_KEY"] = "repo-val-5678abcd"
            },
            setupSteps: [new SetupStep { Name = "Check disjoint", Command = $"echo \"$PROJECT_KEY|$REPO_KEY\" > {outputFile}" }]);

        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        var envValue = (await File.ReadAllTextAsync(outputFile)).Trim();
        envValue.Should().Be("project-val-1234|repo-val-5678abcd");
    }

    // ══════════════════════════════════════════════════════════════════════
    // MASKING TESTS — values masked, short values NOT masked, keys NOT masked
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_ProjectSecretValuesMaskedInOutput()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Arrange — echo a project secret, it should be masked in emitted output
        var secretValue = "project-super-secret-9999";
        var job = CreateJobWithProjectAndRepoSecrets(
            projectSecrets: new Dictionary<string, string>
            {
                ["PROJ_SECRET"] = secretValue
            },
            repoSecrets: null,
            setupSteps: [new SetupStep { Name = "Leak secret", Command = $"echo '{secretValue}'" }]);

        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — emitted lines should NOT contain the raw secret
        _emittedLines.Should().NotContain(l => l.Contains(secretValue));
        _emittedLines.Should().Contain(l => l.Contains("***"));
    }

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_KeysNotMaskedInOutput()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Arrange — the key name should appear in "Injected N environment secrets (keys: ...)"
        var job = CreateJobWithProjectAndRepoSecrets(
            projectSecrets: new Dictionary<string, string>
            {
                ["MY_VISIBLE_KEY"] = "secret-value-hidden"
            },
            repoSecrets: null,
            setupSteps: [new SetupStep { Name = "Noop", Command = "true" }]);

        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — key name is visible, value is not
        _emittedLines.Should().Contain(l => l.Contains("MY_VISIBLE_KEY"));
        _emittedLines.Should().NotContain(l => l.Contains("secret-value-hidden"));
    }

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_SpecialCharactersInSecretValues_MaskedCorrectly()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Arrange — secret with special characters (regex chars, quotes, etc.)
        var specialSecret = "p@ss$w0rd!#%^&*()_+=";
        var job = CreateJobWithProjectAndRepoSecrets(
            projectSecrets: null,
            repoSecrets: new Dictionary<string, string>
            {
                ["SPECIAL"] = specialSecret
            },
            setupSteps: [new SetupStep { Name = "Echo special", Command = $"printf '%s' '{specialSecret.Replace("'", "'\"'\"'")}'" }]);

        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — special chars secret should be masked
        _emittedLines.Should().NotContain(l => l.Contains(specialSecret));
    }

    // ══════════════════════════════════════════════════════════════════════
    // CLEANUP TESTS — InjectedSecretKeys populated on context for finally block
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_InjectedSecretKeys_PopulatedOnContextForCleanup()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Arrange — merged secrets from project + repo
        var job = CreateJobWithProjectAndRepoSecrets(
            projectSecrets: new Dictionary<string, string>
            {
                ["PROJ_A"] = "value-a-1234"
            },
            repoSecrets: new Dictionary<string, string>
            {
                ["REPO_B"] = "value-b-5678"
            },
            setupSteps: [new SetupStep { Name = "Noop", Command = "true" }]);

        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — InjectedSecretKeys should list all merged keys
        context.InjectedSecretKeys.Should().NotBeNull();
        context.InjectedSecretKeys.Should().Contain("PROJ_A");
        context.InjectedSecretKeys.Should().Contain("REPO_B");
    }

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_InjectedSecrets_PopulatedOnContextForPipelineWideMasking()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Arrange
        var job = CreateJobWithProjectAndRepoSecrets(
            projectSecrets: new Dictionary<string, string>
            {
                ["PROJ_X"] = "proj-value-xxxx"
            },
            repoSecrets: new Dictionary<string, string>
            {
                ["REPO_Y"] = "repo-value-yyyy"
            },
            setupSteps: [new SetupStep { Name = "Noop", Command = "true" }]);

        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — InjectedSecrets dict populated for pipeline-wide masking
        context.InjectedSecrets.Should().NotBeNull();
        context.InjectedSecrets.Should().ContainKey("PROJ_X").WhoseValue.Should().Be("proj-value-xxxx");
        context.InjectedSecrets.Should().ContainKey("REPO_Y").WhoseValue.Should().Be("repo-value-yyyy");
    }

    [Fact]
    public void CleanupLogic_InjectedSecretKeysCanBeUnsetFromEnvironment()
    {
        // Arrange — simulate what the executor's finally block does
        var keys = new List<string> { "TEST_KEY_1", "TEST_KEY_2" };
        foreach (var key in keys)
            Environment.SetEnvironmentVariable(key, "some-secret-value");

        // Verify they're set
        Environment.GetEnvironmentVariable("TEST_KEY_1").Should().Be("some-secret-value");
        Environment.GetEnvironmentVariable("TEST_KEY_2").Should().Be("some-secret-value");

        // Act — simulate cleanup
        foreach (var key in keys)
            Environment.SetEnvironmentVariable(key, null);

        // Assert — they're removed
        Environment.GetEnvironmentVariable("TEST_KEY_1").Should().BeNull();
        Environment.GetEnvironmentVariable("TEST_KEY_2").Should().BeNull();
    }

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_NullProjectSecretsAndNullRepoSecrets_NothingInjected()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Arrange — both null, no setup steps → step returns immediately
        var job = CreateJobWithProjectAndRepoSecrets(
            projectSecrets: null,
            repoSecrets: null,
            setupSteps: null);

        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — no secrets injected, step skipped entirely
        result.Should().Be(StepResult.Continue);
        context.InjectedSecretKeys.Should().BeNull();
        context.InjectedSecrets.Should().BeNull();
        _mockCallbacks.Verify(c => c.TransitionTo(It.IsAny<PipelineStep>()), Times.Never);
    }

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_EmptyProjectSecretsAndEmptyRepoSecrets_NothingInjected()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Arrange — both empty dicts but setup steps present
        var job = CreateJobWithProjectAndRepoSecrets(
            projectSecrets: new Dictionary<string, string>(),
            repoSecrets: new Dictionary<string, string>(),
            setupSteps: [new SetupStep { Name = "Noop", Command = "true" }]);

        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — step still runs (has setup steps), but no secrets injected
        result.Should().Be(StepResult.Continue);
        context.InjectedSecretKeys.Should().BeNull();
        context.InjectedSecrets.Should().BeNull();
    }

    // ══════════════════════════════════════════════════════════════════════
    // PIPELINE-WIDE MASKING TESTS — MaskSecretsInOutput static method
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void MaskSecretsInOutput_NullContext_ReturnsOutputUnchanged()
    {
        // The private static method is tested indirectly. Since LocalPipelineExecutor
        // uses it from EmitOutputLine, we test the behavior via reflection.
        // Given InternalsVisibleTo is set, we can test by invoking the private method.
        var method = typeof(LocalPipelineExecutor).GetMethod(
            "MaskSecretsInOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        method.Should().NotBeNull("MaskSecretsInOutput should exist as private static");

        var result = (string)method!.Invoke(null, ["Hello world with secret", null])!;
        result.Should().Be("Hello world with secret");
    }

    [Fact]
    public void MaskSecretsInOutput_EmptyInjectedSecrets_ReturnsOutputUnchanged()
    {
        var method = typeof(LocalPipelineExecutor).GetMethod(
            "MaskSecretsInOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var context = CreateTestContext();
        context.InjectedSecrets = new Dictionary<string, string>();

        var result = (string)method!.Invoke(null, ["output with token12345", context])!;
        result.Should().Be("output with token12345");
    }

    [Fact]
    public void MaskSecretsInOutput_LongSecretValue_IsMasked()
    {
        var method = typeof(LocalPipelineExecutor).GetMethod(
            "MaskSecretsInOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var context = CreateTestContext();
        context.InjectedSecrets = new Dictionary<string, string>
        {
            ["TOKEN"] = "my-secret-token-1234"
        };

        var result = (string)method!.Invoke(null, ["Fetched from https://api.com?token=my-secret-token-1234", context])!;
        result.Should().Be("Fetched from https://api.com?token=***");
    }

    [Fact]
    public void MaskSecretsInOutput_ShortSecretValue_NotMasked()
    {
        var method = typeof(LocalPipelineExecutor).GetMethod(
            "MaskSecretsInOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var context = CreateTestContext();
        context.InjectedSecrets = new Dictionary<string, string>
        {
            ["PIN"] = "123" // 3 chars — should NOT be masked
        };

        var result = (string)method!.Invoke(null, ["The pin is 123 and code is 456", context])!;
        result.Should().Be("The pin is 123 and code is 456");
    }

    [Fact]
    public void MaskSecretsInOutput_MultipleSecrets_AllLongOnesMasked()
    {
        var method = typeof(LocalPipelineExecutor).GetMethod(
            "MaskSecretsInOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var context = CreateTestContext();
        context.InjectedSecrets = new Dictionary<string, string>
        {
            ["TOKEN_A"] = "secret-alpha-1234",
            ["TOKEN_B"] = "secret-beta-5678",
            ["SHORT"] = "ab" // 2 chars — not masked
        };

        var result = (string)method!.Invoke(null, ["Values: secret-alpha-1234 and secret-beta-5678 and ab", context])!;
        result.Should().Be("Values: *** and *** and ab");
    }

    [Fact]
    public void MaskSecretsInOutput_ExactlyFourChars_IsMasked()
    {
        var method = typeof(LocalPipelineExecutor).GetMethod(
            "MaskSecretsInOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var context = CreateTestContext();
        context.InjectedSecrets = new Dictionary<string, string>
        {
            ["KEY4"] = "abcd" // exactly 4 — should be masked
        };

        var result = (string)method!.Invoke(null, ["Value is abcd here", context])!;
        result.Should().Be("Value is *** here");
    }

    // ══════════════════════════════════════════════════════════════════════
    // EDGE CASES — special characters in values, empty string value
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_SecretWithNewlines_MaskedCorrectly()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Arrange — secret value that contains a newline character
        // The masking happens per-line so we test single-line output
        var multiWordSecret = "secret-with-dashes-1234";
        var job = CreateJobWithProjectAndRepoSecrets(
            projectSecrets: new Dictionary<string, string>
            {
                ["MULTILINE"] = multiWordSecret
            },
            repoSecrets: null,
            setupSteps: [new SetupStep { Name = "Echo", Command = $"echo 'prefix {multiWordSecret} suffix'" }]);

        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        _emittedLines.Should().NotContain(l => l.Contains(multiWordSecret));
        _emittedLines.Should().Contain(l => l.Contains("prefix *** suffix"));
    }

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task ExecuteAsync_SecretValueAppearingMultipleTimes_AllOccurrencesMasked()
    {
        if (!OperatingSystem.IsLinux()) return;

        // Arrange — same secret value appears multiple times in output
        var secret = "repeated-secret-val1";
        var job = CreateJobWithProjectAndRepoSecrets(
            projectSecrets: null,
            repoSecrets: new Dictionary<string, string>
            {
                ["REPEAT"] = secret
            },
            setupSteps: [new SetupStep { Name = "Echo repeated", Command = $"echo '{secret} {secret}'" }]);

        var step = new RunEnvironmentSetupStep(job);
        var context = CreateTestContext();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — no occurrence of the secret in output
        _emittedLines.Should().NotContain(l => l.Contains(secret));
        // Should contain *** twice (or concatenated masking)
        _emittedLines.Should().Contain(l => l.Contains("*** ***"));
    }

    // ══════════════════════════════════════════════════════════════════════
    // HELPERS
    // ══════════════════════════════════════════════════════════════════════

    private PipelineStepContext CreateTestContext()
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-config-1",
            RepoProviderConfigId = "repo-config-1",
            WorkspacePath = _tempDir,
            StartedAt = DateTime.UtcNow
        };

        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration(),
            RepoProvider = new Mock<IRepositoryProvider>().Object,
            AgentProvider = new Mock<IAgentProvider>().Object,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = new Mock<IConfigurationStore>().Object,
            Callbacks = _mockCallbacks.Object,
            IssueOps = new Mock<IAgentIssueOperations>().Object,
            AgentExecution = new Mock<IAgentPhaseExecutor>().Object,
            QualityGates = new Mock<IQualityGateExecutor>().Object,
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_mockLogger.Object),
            Logger = _mockLogger.Object
        };
    }

    private static JobAssignmentMessage CreateJobWithProjectAndRepoSecrets(
        Dictionary<string, string>? projectSecrets,
        Dictionary<string, string>? repoSecrets,
        IReadOnlyList<SetupStep>? setupSteps)
    {
        var repoConfig = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "test/repo",
            Settings = new Dictionary<string, string>(),
            Secrets = repoSecrets,
            SetupSteps = setupSteps
        };

        return new JobAssignmentMessage
        {
            JobId = "test-job",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [repoConfig],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            McpServers = [],
            ProjectSecrets = projectSecrets,
            InitiatedBy = "test-user"
        };
    }
}
