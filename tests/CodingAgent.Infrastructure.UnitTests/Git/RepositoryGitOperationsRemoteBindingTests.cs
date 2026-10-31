using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Git;
using CodingAgent.Infrastructure.Resilience;
using CodingAgent.Pipeline.Models;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Polly;

namespace CodingAgent.Infrastructure.UnitTests.Git;

/// <summary>
/// Integration-level tests that verify GitRemoteBinding prevents workspace config tampering
/// from redirecting git credentials to a foreign server.
/// Uses a loopback HTTP server that speaks enough git-smart-HTTP to answer 401 challenges,
/// so no real git server is needed.
/// </summary>
public sealed class RepositoryGitOperationsRemoteBindingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rgo-binding-{Guid.NewGuid():N}");

    public RepositoryGitOperationsRemoteBindingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── Loopback server ───────────────────────────────────────────────────────

    private sealed class LoopbackGitServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<(string Line, string? Credentials)> _requests = [];

        public LoopbackGitServer()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public string? RedirectTo { get; set; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Url(string path) => $"http://127.0.0.1:{Port}/{path}";

        public IReadOnlyList<(string Line, string? Credentials)> Requests
        {
            get { lock (_requests) return _requests.ToList(); }
        }

        public void Dispose() => _listener.Stop();

        private async Task AcceptLoop()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleAsync(client));
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                while (await reader.ReadLineAsync() is { } first)
                {
                    string? credentials = null;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    {
                        if (line.StartsWith("Authorization: Basic ", StringComparison.OrdinalIgnoreCase))
                            credentials = Encoding.UTF8.GetString(Convert.FromBase64String(line["Authorization: Basic ".Length..]));
                    }

                    lock (_requests) _requests.Add((first, credentials));

                    var response = RedirectTo is { } target && credentials is null
                        ? $"HTTP/1.1 302 Found\r\nLocation: {target}\r\nContent-Length: 0\r\n\r\n"
                        : "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"git\"\r\nContent-Length: 0\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                }
            }
        }
    }

    // ── Workspace setup helpers ───────────────────────────────────────────────

    /// <summary>Creates an initialized git repo with one commit and an origin remote pointing at the canonical server.</summary>
    private string CreateWorkspace(string canonicalUrl)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Repository.Init(path);
        using var repo = new Repository(path);
        var sig = new Signature("test", "test@example.com", DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(path, "readme.txt"), "init");
        Commands.Stage(repo, "readme.txt");
        repo.Commit("init", sig, sig);
        repo.Network.Remotes.Add("origin", canonicalUrl);
        return path;
    }

    private static void ApplyTamper(string path, string tamper, string canonicalRoot, string canaryRoot)
    {
        var gitConfig = Path.Combine(path, ".git", "config");
        var forward = path.Replace('\\', '/');

        switch (tamper)
        {
            case "pushurl":
                File.AppendAllText(gitConfig, $"\n[remote \"origin\"]\n\tpushurl = {canaryRoot}/o/r.git\n");
                break;
            case "url":
                File.AppendAllText(gitConfig, $"\n[remote \"origin\"]\n\turl = {canaryRoot}/o/r.git\n");
                break;
            case "insteadOf":
                File.AppendAllText(gitConfig, $"\n[url \"{canaryRoot}/\"]\n\tinsteadOf = {canonicalRoot}/\n");
                break;
            case "pushInsteadOf":
                File.AppendAllText(gitConfig, $"\n[url \"{canaryRoot}/\"]\n\tpushInsteadOf = {canonicalRoot}/\n");
                break;
            case "include.path":
            {
                var inc = Path.Combine(path, "inc.cfg");
                File.WriteAllText(inc, $"[url \"{canaryRoot}/\"]\n\tinsteadOf = {canonicalRoot}/\n");
                File.AppendAllText(gitConfig, $"\n[include]\n\tpath = {inc.Replace('\\', '/')}\n");
                break;
            }
            case "includeIf":
            {
                var inc2 = Path.Combine(path, "inc2.cfg");
                File.WriteAllText(inc2, $"[remote \"origin\"]\n\tpushurl = {canaryRoot}/o/r.git\n");
                File.AppendAllText(gitConfig, $"\n[includeIf \"gitdir:{forward}/\"]\n\tpath = {inc2.Replace('\\', '/')}\n");
                break;
            }
            case "http.proxy":
                File.AppendAllText(gitConfig, $"\n[http]\n\tproxy = {canaryRoot}\n");
                break;
            case "remote.proxy":
                File.AppendAllText(gitConfig, $"\n[remote \"origin\"]\n\tproxy = {canaryRoot}\n");
                break;
            case "config.worktree":
            {
                var wt = Path.Combine(path, ".git", "config.worktree");
                File.WriteAllText(wt, $"[url \"{canaryRoot}/\"]\n\tinsteadOf = {canonicalRoot}/\n");
                File.AppendAllText(gitConfig, "\n[core]\n\trepositoryformatversion = 1\n[extensions]\n\tworktreeConfig = true\n");
                break;
            }
        }
    }

    private static string CanaryRoot(LoopbackGitServer canary) => $"http://127.0.0.1:{canary.Port}";
    private static string CanonicalRoot(LoopbackGitServer canonical) => $"http://127.0.0.1:{canonical.Port}";

    // ── Tamper tests ──────────────────────────────────────────────────────────

    public static IEnumerable<object[]> TamperTypes =>
    [
        ["pushurl"], ["url"], ["insteadOf"], ["pushInsteadOf"],
        ["include.path"], ["includeIf"], ["http.proxy"], ["remote.proxy"], ["config.worktree"]
    ];

    [Theory]
    [MemberData(nameof(TamperTypes))]
    public async Task Push_WhenTheWorkspaceConfigIsTampered_SendsTheTokenOnlyToTheProvider(string tamper)
    {
        using var canonical = new LoopbackGitServer();
        using var canary = new LoopbackGitServer();

        var canonicalUrl = canonical.Url("o/r.git");
        var path = CreateWorkspace(canonicalUrl);

        ApplyTamper(path, tamper, CanonicalRoot(canonical), CanaryRoot(canary));

        var branchName = new BranchName("main");
        await Record.ExceptionAsync(() =>
            RepositoryGitOperations.Push(new WorkspacePath(path), canonicalUrl, branchName, false,
                "x-access-token", _ => Task.FromResult("CANARY-TOKEN"), ResiliencePipeline.Empty, default));

        canary.Requests.Should().BeEmpty("token must never reach the canary server");
        canonical.Requests.Should().Contain(r => r.Credentials == "x-access-token:CANARY-TOKEN");
    }

    [Theory]
    [MemberData(nameof(TamperTypes))]
    public async Task Pull_WhenTheWorkspaceConfigIsTampered_SendsTheTokenOnlyToTheProvider(string tamper)
    {
        using var canonical = new LoopbackGitServer();
        using var canary = new LoopbackGitServer();

        var canonicalUrl = canonical.Url("o/r.git");
        var path = CreateWorkspace(canonicalUrl);

        ApplyTamper(path, tamper, CanonicalRoot(canonical), CanaryRoot(canary));

        await Record.ExceptionAsync(() =>
            RepositoryGitOperations.Pull(new WorkspacePath(path), canonicalUrl, new BranchName("main"),
                "x-access-token", "CANARY-TOKEN", ResiliencePipeline.Empty, default));

        canary.Requests.Should().BeEmpty("token must never reach the canary server");
        canonical.Requests.Should().Contain(r => r.Credentials == "x-access-token:CANARY-TOKEN");
    }

    [Theory]
    [MemberData(nameof(TamperTypes))]
    public async Task MergeFromBase_WhenTheWorkspaceConfigIsTampered_SendsTheTokenOnlyToTheProvider(string tamper)
    {
        using var canonical = new LoopbackGitServer();
        using var canary = new LoopbackGitServer();

        var canonicalUrl = canonical.Url("o/r.git");
        var path = CreateWorkspace(canonicalUrl);

        ApplyTamper(path, tamper, CanonicalRoot(canonical), CanaryRoot(canary));

        await Record.ExceptionAsync(() =>
            RepositoryGitOperations.MergeFromBase(new WorkspacePath(path), canonicalUrl, new BranchName("main"),
                "x-access-token", "CANARY-TOKEN", ResiliencePipeline.Empty, default));

        canary.Requests.Should().BeEmpty("token must never reach the canary server");
        canonical.Requests.Should().Contain(r => r.Credentials == "x-access-token:CANARY-TOKEN");
    }

    [Fact]
    public async Task Pull_WhenTheProviderRedirectsToAnotherServer_RefusesTheToken()
    {
        using var canonical = new LoopbackGitServer();
        using var canary = new LoopbackGitServer();

        // Redirect from canonical to canary. The Location URL must include the service query to
        // prevent a libgit2 0.32 segfault (tested and confirmed).
        canonical.RedirectTo = canary.Url("x.git/info/refs?service=git-upload-pack");

        var canonicalUrl = canonical.Url("o/r.git");
        var path = CreateWorkspace(canonicalUrl);

        var ex = await Record.ExceptionAsync(() =>
            RepositoryGitOperations.Pull(new WorkspacePath(path), canonicalUrl, new BranchName("main"),
                "x-access-token", "TOKEN", ResiliencePipeline.Empty, default));

        ex.Should().BeOfType<LibGit2SharpException>()
            .Which.Message.Should().Contain(PushErrorClassifier.RemoteRefusedMarker);

        // The canary may receive an unauthenticated request (the redirect target) but never credentials.
        canary.Requests.Should().NotContain(r => r.Credentials != null,
            "credentials must never be sent to the redirect target");
        canonical.Requests.Should().NotContain(r => r.Credentials != null,
            "canonical server should not receive credentials when it redirects away");
    }

    [Fact]
    public async Task Clone_SendsTheTokenToTheCloneUrlsServer()
    {
        using var canonical = new LoopbackGitServer();
        var canonicalUrl = canonical.Url("o/r.git");
        var dest = Path.Combine(_root, "clone-dest");

        await Record.ExceptionAsync(() =>
            RepositoryGitOperations.Clone(new WorkspacePath(dest), canonicalUrl, new BranchName("main"),
                "x-access-token", "CLONE-TOKEN", ResiliencePipeline.Empty, default));

        canonical.Requests.Should().Contain(r => r.Credentials == "x-access-token:CLONE-TOKEN");
    }

    // ── CredentialsFor unit tests ─────────────────────────────────────────────

    [Theory]
    [InlineData("https://gitlab.example.com:8443/group/sub/repo.git")]
    [InlineData("https://gitlab.example.com:8443/other/repo.git")]
    [InlineData("https://GITLAB.example.com:8443/group/sub/repo.git")]
    [InlineData("https://oauth2:x@gitlab.example.com:8443/group/sub/repo.git")]
    public void CredentialsFor_AcceptsTheSameServer(string url)
    {
        const string boundUrl = "https://gitlab.example.com:8443/group/sub/repo.git";
        var handler = GitRemoteBinding.CredentialsFor(boundUrl, "oauth2", "TOKEN");

        var result = handler(url, "", SupportedCredentialTypes.UsernamePassword);

        var creds = result.Should().BeOfType<UsernamePasswordCredentials>().Which;
        creds.Username.Should().Be("oauth2");
        creds.Password.Should().Be("TOKEN");
    }

    [Theory]
    [InlineData("http://gitlab.example.com:8443/group/sub/repo.git")]
    [InlineData("https://gitlab.example.com/group/sub/repo.git")]
    [InlineData("https://gitlab.example.com:9443/group/sub/repo.git")]
    [InlineData("https://gitlab.example.com.other.test:8443/group/sub/repo.git")]
    [InlineData("https://other.test:8443/group/sub/repo.git")]
    [InlineData("not a url")]
    public void CredentialsFor_RefusesAnyOtherServer(string url)
    {
        const string boundUrl = "https://gitlab.example.com:8443/group/sub/repo.git";
        var handler = GitRemoteBinding.CredentialsFor(boundUrl, "oauth2", "TOKEN");

        var act = () => handler(url, "", SupportedCredentialTypes.UsernamePassword);

        var ex = act.Should().Throw<InvalidOperationException>().Which;
        ex.Message.Should().StartWith(PushErrorClassifier.RemoteRefusedMarker);
        ex.Message.Should().NotContain("group/sub", "path must not be logged");
    }

    // ── ResetRemoteConfig unit tests ──────────────────────────────────────────

    [Fact]
    public void ResetRemoteConfig_RemovesEveryOverride_AndWritesTheCanonicalRemote()
    {
        var path = Path.Combine(_root, "reset-test");
        Directory.CreateDirectory(path);
        Repository.Init(path);

        var sig = new Signature("t", "t@t.com", DateTimeOffset.UtcNow);
        using (var repo = new Repository(path))
        {
            File.WriteAllText(Path.Combine(path, "f.txt"), "x");
            Commands.Stage(repo, "f.txt");
            repo.Commit("init", sig, sig);
            repo.Network.Remotes.Add("origin", "http://a.test/x.git");
        }

        const string polluted = """

[remote "origin"]
	url = http://a.test/x.git
	url = http://b.test/x.git
	fetch = +refs/*:refs/*
	fetch = +refs/heads/x:refs/remotes/origin/main
	pushurl = http://c.test/x.git
[include]
	path = /tmp/a.cfg
	path = /tmp/b.cfg
[HTTP]
	proxy = http://p.test
[http "https://x.test/"]
	proxy = http://p2.test
[URL "http://e.test/"]
	insteadOf = https://y.test/
	insteadOf = https://z.test/
[includeIf "gitdir:/x/"]
	path = /tmp/c.cfg
""";
        File.AppendAllText(Path.Combine(path, ".git", "config"), polluted);

        const string canonicalUrl = "https://canonical.example.com/g/r.git";
        using (var repo = new Repository(path))
            GitRemoteBinding.ResetRemoteConfig(repo, canonicalUrl);

        using var clean = new Repository(path);
        clean.Config.Find(@"^(url\.|http\.|include\.|includeif\.)", ConfigurationLevel.Local)
            .Should().BeEmpty("all overriding keys must be removed");
        clean.Network.Remotes["origin"].Url.Should().Be(canonicalUrl);
        clean.Network.Remotes["origin"].PushUrl.Should().Be(canonicalUrl);
        clean.Network.Remotes["origin"].FetchRefSpecs.Select(r => r.Specification)
            .Should().ContainSingle().Which.Should().Be(GitRemoteBinding.StandardFetchRefSpec);
    }

    [Fact]
    public void ResetRemoteConfig_KeepsTrackingRefsAndTheTrackedBranch()
    {
        var originPath = Path.Combine(_root, "rw-origin");
        var workPath = Path.Combine(_root, "rw-work");
        Directory.CreateDirectory(originPath);
        Repository.Init(originPath);

        var sig = new Signature("t", "t@t.com", DateTimeOffset.UtcNow);
        using (var origin = new Repository(originPath))
        {
            File.WriteAllText(Path.Combine(originPath, "f.txt"), "x");
            Commands.Stage(origin, "f.txt");
            origin.Commit("init", sig, sig);
            origin.CreateBranch("feature/x");
        }

        Repository.Clone(originPath, workPath);
        RepositoryGitOperations.CheckoutRemoteBranch(new WorkspacePath(workPath), new BranchName("feature/x"));

        using var repo = new Repository(workPath);
        GitRemoteBinding.ResetRemoteConfig(repo, originPath);

        repo.Branches["origin/feature/x"].Should().NotBeNull("tracking ref must survive reset");
        repo.Head.TrackedBranch.Should().NotBeNull("tracked branch must survive reset");
    }

    [Fact]
    public void ResetRemoteConfig_LeavesANestedRepositoryAlone()
    {
        var outerPath = Path.Combine(_root, "nested-outer");
        var brainPath = Path.Combine(outerPath, ".brain");
        Directory.CreateDirectory(outerPath);
        Directory.CreateDirectory(brainPath);

        var sig = new Signature("t", "t@t.com", DateTimeOffset.UtcNow);
        string brainOriginalUrl = "https://brain.example.com/g/brain.git";

        // Init outer repo with an origin
        Repository.Init(outerPath);
        using (var outer = new Repository(outerPath))
        {
            File.WriteAllText(Path.Combine(outerPath, "f.txt"), "x");
            Commands.Stage(outer, "f.txt");
            outer.Commit("init", sig, sig);
            outer.Network.Remotes.Add("origin", "https://code.example.com/g/repo.git");
        }

        // Init nested brain repo with a different origin
        Repository.Init(brainPath);
        using (var brain = new Repository(brainPath))
        {
            File.WriteAllText(Path.Combine(brainPath, "f.txt"), "y");
            Commands.Stage(brain, "f.txt");
            brain.Commit("init", sig, sig);
            brain.Network.Remotes.Add("origin", brainOriginalUrl);
        }

        const string canonicalUrl = "https://code.example.com/g/repo.git";
        using (var outer = new Repository(outerPath))
            GitRemoteBinding.ResetRemoteConfig(outer, canonicalUrl);

        using var brainAfter = new Repository(brainPath);
        brainAfter.Network.Remotes["origin"].Url.Should().Be(brainOriginalUrl,
            "nested repository's remote must not be affected by outer reset");
    }

    [Fact]
    public async Task Pull_FromTheCanonicalRemote_StillFastForwards()
    {
        // Ensure the static constructor has run before Repository.Init so that
        // init.defaultBranch from ~/.gitconfig is suppressed and libgit2 uses "master".
        RuntimeHelpers.RunClassConstructor(typeof(RepositoryGitOperations).TypeHandle);

        var originPath = Path.Combine(_root, "ff-origin");
        var workPath = Path.Combine(_root, "ff-work");
        Directory.CreateDirectory(originPath);
        Repository.Init(originPath);

        var sig = new Signature("t", "t@t.com", DateTimeOffset.UtcNow);
        using (var origin = new Repository(originPath))
        {
            File.WriteAllText(Path.Combine(originPath, "f.txt"), "v1");
            Commands.Stage(origin, "f.txt");
            origin.Commit("initial", sig, sig);
        }

        Repository.Clone(originPath, workPath);

        // Add a new commit on origin
        string newCommitSha;
        using (var origin = new Repository(originPath))
        {
            File.WriteAllText(Path.Combine(originPath, "f.txt"), "v2");
            Commands.Stage(origin, "f.txt");
            var c = origin.Commit("update", sig, sig);
            newCommitSha = c.Sha;
        }

        await RepositoryGitOperations.Pull(new WorkspacePath(workPath), originPath,
            new BranchName("master"), "user", "token", ResiliencePipeline.Empty, default);

        using var work = new Repository(workPath);
        work.Head.Tip.Sha.Should().Be(newCommitSha, "fast-forward pull must update HEAD to the new commit");
    }

    [Fact]
    public void ConfigSearchPaths_PointAtADirectoryThatDoesNotExist()
    {
        // Trigger the static constructor if not already run
        RuntimeHelpers.RunClassConstructor(typeof(RepositoryGitOperations).TypeHandle);

        foreach (var level in new[] { ConfigurationLevel.Global, ConfigurationLevel.Xdg, ConfigurationLevel.System })
        {
            GlobalSettings.GetConfigSearchPaths(level)
                .Should().Equal([RepositoryGitOperations.NoConfigDirectory],
                    $"level {level} must point at the no-config directory");
        }

        Directory.Exists(RepositoryGitOperations.NoConfigDirectory)
            .Should().BeFalse("the no-config directory must not exist");

        var tempRepo = Path.Combine(_root, "cfg-test");
        Directory.CreateDirectory(tempRepo);
        Repository.Init(tempRepo);
        using var repo = new Repository(tempRepo);
        repo.Config.HasConfig(ConfigurationLevel.Global)
            .Should().BeFalse("global config must be absent after redirecting search paths");
    }
}
