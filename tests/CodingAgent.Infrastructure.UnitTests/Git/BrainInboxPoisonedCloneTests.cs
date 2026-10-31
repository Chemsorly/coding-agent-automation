using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;
using LibGit2Sharp;
using Serilog;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Security: proves that tampering with the .brain/ working clone cannot poison the remote brain repo.
/// The new WriteInboxAsync writes through the provider API only; it reads nothing from .brain/.
/// No Category trait (T12).
/// </summary>
public class BrainInboxPoisonedCloneTests : IDisposable
{
    private const string ReadmeContent = "# Brain Repository\n";
    private const string SkillContent = "<!-- brain:v2 skill -->\n- [2+] For original, use original. (#orig).\n";

    private readonly BrainRepoFixture _fixture;
    private readonly BrainUpdateService _updateService;
    private readonly BrainSyncService _sut;
    private readonly ILogger _logger;

    public BrainInboxPoisonedCloneTests()
    {
        _fixture = BrainRepoFixture.Create(new Dictionary<string, string>
        {
            ["README.md"] = ReadmeContent,
            ["general/SKILL.md"] = SkillContent
        });
        _logger = new LoggerConfiguration().CreateLogger();
        _updateService = new BrainUpdateService(_logger);
        _sut = new BrainSyncService(_updateService, _logger);
    }

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("agent_commit_only")]
    [InlineData("origin_main_moved")]
    [InlineData("origin_main_deleted")]
    [InlineData("head_on_side_branch")]
    [InlineData("forged_inbox_file")]
    public async Task WriteInbox_PoisonedClone_RemoteGetsOnlyTheInboxFile(string tamperCase)
    {
        // Arrange: create the agent's .brain/ clone
        var clonePath = _fixture.Clone(".brain");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "org/repo#99",
            IssueTitle = "Tamper test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.GetDirectoryName(clonePath)!, // workspace has .brain/ inside
            StartedAt = DateTime.UtcNow
        };

        // Make an agent commit (not pushed) on general/SKILL.md
        using (var repo = new Repository(clonePath))
        {
            var skillPath = Path.Combine(clonePath, "general", "SKILL.md");
            File.WriteAllText(skillPath, SkillContent + "- [1] Agent added this. (#agent).\n");
            Commands.Stage(repo, "general/SKILL.md");
            var sig = new Signature("agent", "agent@example.com", DateTimeOffset.UtcNow);
            repo.Commit("agent: add skill", sig, sig);
        }

        // Apply tamper
        ApplyTamper(tamperCase, clonePath);

        var provider = _fixture.Provider();

        // Act
        var inboxPath = BrainLayout.InboxFileName(today, run.RunId);
        await _sut.WriteInboxAsync(run, provider.Object, inboxPath, "inbox text content", CancellationToken.None);

        // Assert
        run.BrainUpdatesPushed.Should().BeTrue($"WriteInboxAsync must succeed for tamper case '{tamperCase}'");

        var remoteFiles = _fixture.RemoteFiles();
        var expectedKeys = new HashSet<string> { "README.md", "general/SKILL.md", inboxPath };
        remoteFiles.Keys.Should().BeEquivalentTo(expectedKeys,
            $"remote must contain only the seed files and the inbox file for tamper case '{tamperCase}'");
        remoteFiles["general/SKILL.md"].Should().Be(SkillContent,
            $"agent's unpushed skill changes must not appear in remote for tamper case '{tamperCase}'");
    }

    private static void ApplyTamper(string tamperCase, string clonePath)
    {
        switch (tamperCase)
        {
            case "agent_commit_only":
                // No extra tamper — agent's commit is already made but not pushed.
                break;

            case "origin_main_moved":
                // Forge: point origin/main to the agent's (unpushed) HEAD commit.
                using (var repo = new Repository(clonePath))
                {
                    repo.Refs.UpdateTarget("refs/remotes/origin/main", repo.Head.Tip.Sha);
                }
                break;

            case "origin_main_deleted":
                // Forge: remove the remote tracking ref entirely.
                using (var repo = new Repository(clonePath))
                {
                    repo.Refs.Remove("refs/remotes/origin/main");
                }
                break;

            case "head_on_side_branch":
                // Forge: create a side branch at the seed commit and check it out,
                // leaving refs/heads/main on the agent's commit.
                using (var repo = new Repository(clonePath))
                {
                    // Find the parent (seed) commit
                    var seedCommit = repo.Head.Tip.Parents.First();
                    var sideBranch = repo.CreateBranch("side", seedCommit);
                    Commands.Checkout(repo, sideBranch);
                }
                break;

            case "forged_inbox_file":
                // Forge: also commit an extra inbox file (to try to poison the remote).
                using (var repo = new Repository(clonePath))
                {
                    var inboxDir = Path.Combine(clonePath, "inbox");
                    Directory.CreateDirectory(inboxDir);
                    var forgedPath = Path.Combine(inboxDir, "2026-10-30_other.md");
                    File.WriteAllText(forgedPath, "<!-- brain:v2 inbox -->\nforged content\n");
                    Commands.Stage(repo, "inbox/2026-10-30_other.md");
                    var sig = new Signature("agent", "agent@example.com", DateTimeOffset.UtcNow);
                    repo.Commit("agent: forge inbox", sig, sig);
                }
                break;

            default:
                throw new ArgumentException($"Unknown tamper case: {tamperCase}");
        }
    }
}
