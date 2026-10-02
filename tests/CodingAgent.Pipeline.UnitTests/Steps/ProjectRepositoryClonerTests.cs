using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="ProjectRepositoryCloner"/>: a clone of another project repository is sealed, so nothing in
/// the workspace keeps the repository's token and the clone cannot push. The seal tests run real git.
/// </summary>
public class ProjectRepositoryClonerTests : IDisposable
{
    private const string TokenUrl = "https://oauth2:glpat-secret-token@gitlab.example.com/acme/shop-api.git";

    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly string _workspacePath = Path.Combine(Path.GetTempPath(), $"project-clone-seal-{Guid.NewGuid():N}");

    public ProjectRepositoryClonerTests() => Directory.CreateDirectory(_workspacePath);

    public void Dispose()
    {
        if (!Directory.Exists(_workspacePath))
            return;
        // Git object files are read-only on Windows.
        foreach (var file in Directory.EnumerateFiles(_workspacePath, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_workspacePath, recursive: true);
    }

    /// <summary>A repository the way a token clone leaves it: the token in the remote URL, FETCH_HEAD and the reflog.</summary>
    private static async Task CreateTokenCloneAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        await GitProcessRunner.RunAsync(dir, "init --quiet", CancellationToken.None);
        await GitProcessRunner.RunAsync(dir, $"remote add origin {TokenUrl}", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(dir, ".git", "FETCH_HEAD"), $"0123 branch 'main' of {TokenUrl}\n");
        Directory.CreateDirectory(Path.Combine(dir, ".git", "logs"));
        await File.WriteAllTextAsync(Path.Combine(dir, ".git", "logs", "HEAD"), $"0000 0123 Agent <a@b> 0 +0000\tclone: from {TokenUrl}\n");
    }

    private static string AllGitFiles(string dir) => string.Concat(
        Directory.EnumerateFiles(Path.Combine(dir, ".git"), "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}objects{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText));

    [Fact]
    public async Task SealAsync_TokenClone_KeepsNoToken()
    {
        var dir = Path.Combine(_workspacePath, "shop-api");
        await CreateTokenCloneAsync(dir);

        await ProjectRepositoryCloner.SealAsync(dir, CancellationToken.None);

        (await GitProcessRunner.RunAsync(dir, "remote get-url origin", CancellationToken.None)).Trim()
            .Should().Be("https://gitlab.example.com/acme/shop-api.git");
        AllGitFiles(dir).Should().NotContain("glpat-secret-token");
    }

    [Fact]
    public async Task SealAsync_CloneOfAWritableRepository_CannotPushToIt()
    {
        // The origin is a local repository that accepts pushes, so only the seal can make the push fail
        await GitProcessRunner.RunAsync(_workspacePath, "init --bare --quiet origin.git", CancellationToken.None);
        await GitProcessRunner.RunAsync(_workspacePath, "clone --quiet origin.git shop-api", CancellationToken.None);
        var dir = Path.Combine(_workspacePath, "shop-api");
        await GitProcessRunner.RunAsync(dir, "-c user.name=Agent -c user.email=agent@example.com -c commit.gpgsign=false commit --allow-empty --quiet -m change", CancellationToken.None);

        await ProjectRepositoryCloner.SealAsync(dir, CancellationToken.None);

        await FluentActions.Awaiting(() => GitProcessRunner.RunAsync(dir, "push origin HEAD:refs/heads/agent-change", CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        (await GitProcessRunner.RunAsync(Path.Combine(_workspacePath, "origin.git"), "for-each-ref refs/heads", CancellationToken.None))
            .Should().BeEmpty("nothing reached the origin");
    }

    [Fact]
    public async Task CloneAsync_ProviderClonesWithAToken_TheCloneIsSealed()
    {
        var api = new RepositoryTarget { TemplateName = "api", Description = "", RepoProviderId = "repo-api" };
        var provider = new Mock<IRepositoryProvider>();
        provider.Setup(p => p.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns<WorkspacePath, CancellationToken>((path, _) => CreateTokenCloneAsync(path.Value));

        await ProjectRepositoryCloner.CloneAsync([("api", provider.Object)], [api], ".agent/project-repos", BuildContext(), CancellationToken.None);

        api.LocalPath.Should().Be(".agent/project-repos/api");
        AllGitFiles(Path.Combine(_workspacePath, ".agent", "project-repos", "api")).Should().NotContain("glpat-secret-token");
    }

    [Fact]
    public async Task CloneAsync_CloneThatCannotBeSealed_IsRemovedAndUnavailable()
    {
        // A .git folder that is no repository: git fails, so the token could not be removed
        var api = new RepositoryTarget { TemplateName = "api", Description = "", RepoProviderId = "repo-api" };
        var provider = new Mock<IRepositoryProvider>();
        provider.Setup(p => p.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns<WorkspacePath, CancellationToken>((path, _) =>
            {
                Directory.CreateDirectory(Path.Combine(path.Value, ".git"));
                File.WriteAllText(Path.Combine(path.Value, ".git", "config"), $"[remote \"origin\"]\n\turl = {TokenUrl}\n");
                return Task.CompletedTask;
            });

        await ProjectRepositoryCloner.CloneAsync([("api", provider.Object)], [api], ".agent/project-repos", BuildContext(), CancellationToken.None);

        api.LocalPath.Should().BeNull();
        Directory.Exists(Path.Combine(_workspacePath, ".agent", "project-repos", "api")).Should().BeFalse();
    }

    [Fact]
    public async Task CloneAsync_BrokenCloneInsideTheWorkspaceRepository_LeavesTheWorkspaceRemoteAlone()
    {
        // The workspace is the run's own repository. From a broken clone inside it, git must not fall back to it, or
        // sealing would strip the run's own token and turn off its push.
        const string ownUrl = "https://oauth2:own-token@gitlab.example.com/acme/shop-web.git";
        await GitProcessRunner.RunAsync(_workspacePath, "init --quiet", CancellationToken.None);
        await GitProcessRunner.RunAsync(_workspacePath, $"remote add origin {ownUrl}", CancellationToken.None);
        var api = new RepositoryTarget { TemplateName = "api", Description = "", RepoProviderId = "repo-api" };
        var provider = new Mock<IRepositoryProvider>();
        provider.Setup(p => p.CloneAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns<WorkspacePath, CancellationToken>((path, _) =>
            {
                Directory.CreateDirectory(Path.Combine(path.Value, ".git"));
                return Task.CompletedTask;
            });

        await ProjectRepositoryCloner.CloneAsync([("api", provider.Object)], [api], ".agent/project-repos", BuildContext(), CancellationToken.None);

        api.LocalPath.Should().BeNull();
        (await GitProcessRunner.RunAsync(_workspacePath, "remote get-url --push origin", CancellationToken.None)).Trim()
            .Should().Be(ownUrl);
    }

    [Theory]
    [InlineData(TokenUrl, "https://gitlab.example.com/acme/shop-api.git")]
    [InlineData("https://x-access-token:ghs_abc@github.com/acme/web.git", "https://github.com/acme/web.git")]
    [InlineData("https://oauth2:glpat-x@gitlab.example.com/acme/shop \"api\".git", "https://gitlab.example.com/acme/shop%20%22api%22.git")]
    [InlineData("https://github.com/acme/web.git", "https://github.com/acme/web.git")]
    [InlineData("git@github.com:acme/web.git", "git@github.com:acme/web.git")]
    public void WithoutCredentials_RemovesOnlyTheUserInfo(string url, string expected)
    {
        ProjectRepositoryCloner.WithoutCredentials(url).Should().Be(expected);
    }

    private PipelineStepContext BuildContext() => new()
    {
        Run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "101",
            IssueTitle = "Add the order endpoint",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            WorkspacePath = _workspacePath
        },
        Config = new PipelineConfiguration(),
        RepoProvider = Mock.Of<IRepositoryProvider>(),
        AgentProvider = Mock.Of<IAgentProvider>(),
        BrainProvider = null,
        PipelineProvider = null,
        Cts = null,
        ConfigStore = Mock.Of<IConfigurationStore>(),
        Callbacks = Mock.Of<IPipelineCallbacks>(),
        IssueOps = Mock.Of<IAgentIssueOperations>(),
        AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
        QualityGates = Mock.Of<IQualityGateExecutor>(),
        BrainSync = null,
        PrOrchestrator = new PullRequestOrchestrator(_logger),
        Logger = _logger
    };
}
