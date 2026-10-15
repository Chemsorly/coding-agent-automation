using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Api.Ui;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.GitLab;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiProviderCollection : ICollectionFixture<ProviderChecksApiFactory>
{
    public const string Name = "UiProviders";
}

/// <summary>
/// The API host with GitHub, GitLab and the issue providers replaced by fakes that record the
/// credentials they are called with, so a test can see which value the API filled in for a mask.
/// </summary>
public sealed class ProviderChecksApiFactory : ApiWebApplicationFactory
{
    public RecordingGitHubValidation GitHub { get; } = new();
    public RecordingGitLabValidation GitLab { get; } = new();
    public Mock<IProviderFactory> Providers { get; } = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<GitHubValidationService>();
        services.AddSingleton<GitHubValidationService>(GitHub);
        services.RemoveAll<GitLabValidationService>();
        services.AddSingleton<GitLabValidationService>(GitLab);
        services.RemoveAll<IProviderFactory>();
        services.AddSingleton(Providers.Object);
    }
}

/// <summary>Accepts every private key but <see cref="BadKey"/>; records the keys it was given.</summary>
public sealed class RecordingGitHubValidation : GitHubValidationService
{
    public const string BadKey = "bad-key";
    public List<string> Keys { get; } = [];

    public override Task<(bool Success, string Message)> ValidateAppCredentialsAsync(
        string apiUrl, string clientId, long installationId, string privateKeyBase64, CancellationToken ct,
        string? owner = null, string? repo = null)
    {
        Keys.Add(privateKeyBase64);
        return Task.FromResult(privateKeyBase64 == BadKey
            ? (false, "Invalid private key: could not decode from base64")
            : (true, owner is null ? "✅ GitHub App credentials validated" : $"✅ {owner}/{repo} — permissions: read, write"));
    }

    public override Task<IReadOnlyList<(string FullName, string Owner, string Name)>> ListRepositoriesWithAppAsync(
        string apiUrl, string clientId, long installationId, string privateKeyBase64, CancellationToken ct)
    {
        Keys.Add(privateKeyBase64);
        return Task.FromResult<IReadOnlyList<(string, string, string)>>([("acme/webapp", "acme", "webapp"), ("acme/api", "acme", "api")]);
    }

    public override Task<(bool Success, string Message)> ValidateActionsAccessAsync(
        string apiUrl, string clientId, long installationId, string privateKeyBase64, string owner, string repo, CancellationToken ct)
    {
        Keys.Add(privateKeyBase64);
        return Task.FromResult((true, "✅ Actions access verified — 3 workflow run(s) found"));
    }
}

/// <summary>Accepts every token; records the tokens it was given.</summary>
public sealed class RecordingGitLabValidation : GitLabValidationService
{
    public List<string> Tokens { get; } = [];

    public override Task<GitLabValidationResult> ValidateAsync(string apiUrl, string accessToken, string projectId, CancellationToken ct)
    {
        Tokens.Add(accessToken);
        return Task.FromResult(new GitLabValidationResult(true, $"group/project-{projectId}", "Maintainer", null));
    }
}

/// <summary>
/// Spec 051 2.1c: provider configs and provider operations on the UI surface. Admin only;
/// credentials come back masked, and every operation fills a mask from the stored config with
/// the same ID, but only with that config's API URL (D4). Operations run through the web's real
/// <see cref="UiProviderClient"/>, so these tests double as its contract tests.
/// </summary>
[Collection(UiProviderCollection.Name)]
public sealed class UiProviderEndpointTests(ProviderChecksApiFactory factory)
{
    private const string IssueId = "8b000000-0000-0000-0000-0000000000a1";
    private const string RepoId = "8b000000-0000-0000-0000-0000000000a2";
    private const string GitLabId = "8b000000-0000-0000-0000-0000000000a3";
    private const string NewId = "8b000000-0000-0000-0000-0000000000a4";
    private const string KeyCanary = "canary-provider-key-4d11";
    private const string TokenCanary = "canary-provider-token-5e22";
    private const string AccessTokenCanary = "canary-provider-access-token-7a44";
    private const string SecretCanary = "canary-provider-secret-6f33";
    private const string Mask = UiSecretMask.Mask;

    private sealed class FixedToken(string token) : IUiAccessTokenSource
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => ValueTask.FromResult<string?>(token);
    }

    private UiProviderClient Providers(AccessGrant? grant = null) =>
        new(factory.CreateClient(), new FixedToken(TestUserTokens.For(grant ?? TestUserTokens.Global(AccessRole.Admin))));

    private HttpClient Http(AccessGrant grant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant));
        return client;
    }

    private static ProviderConfig GitHubIssue() => new()
    {
        Id = IssueId,
        Kind = ProviderKind.Issue,
        ProviderType = ProviderTypes.GitHub,
        DisplayName = "Issues",
        Settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.ApiUrl] = ProviderSettingKeys.DefaultApiUrl,
            [ProviderSettingKeys.ClientId] = "Iv1.client",
            [ProviderSettingKeys.InstallationId] = "456",
            [ProviderSettingKeys.PrivateKeyBase64] = KeyCanary,
            [ProviderSettingKeys.Owner] = "acme",
            [ProviderSettingKeys.Repo] = "webapp",
        },
    };

    private static ProviderConfig GitHubRepo() => new()
    {
        Id = RepoId,
        Kind = ProviderKind.Repository,
        ProviderType = ProviderTypes.GitHub,
        DisplayName = "Repository",
        RepositoryRole = RepositoryRole.Brain,
        RequiredLabels = ["backend"],
        BlacklistedPaths = ["secrets/"],
        SetupSteps = [new SetupStep { Name = "restore", Command = "npm ci" }],
        SteeringContent = "Use pnpm.",
        Secrets = new Dictionary<string, string> { ["NPM_TOKEN"] = SecretCanary },
        Settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.ApiUrl] = ProviderSettingKeys.DefaultApiUrl,
            [ProviderSettingKeys.ClientId] = "Iv1.client",
            [ProviderSettingKeys.InstallationId] = "456",
            [ProviderSettingKeys.PrivateKeyBase64] = KeyCanary,
            [ProviderSettingKeys.Token] = TokenCanary,
            [ProviderSettingKeys.Owner] = "acme",
            [ProviderSettingKeys.Repo] = "webapp",
        },
    };

    private static ProviderConfig GitLabPipeline() => new()
    {
        Id = GitLabId,
        Kind = ProviderKind.Pipeline,
        ProviderType = ProviderTypes.GitLab,
        DisplayName = "GitLab CI",
        Settings = new Dictionary<string, string>
        {
            [ProviderSettingKeys.ApiUrl] = "https://gitlab.example.com/api/v4",
            [ProviderSettingKeys.AccessToken] = AccessTokenCanary,
            [ProviderSettingKeys.ProjectId] = "42",
        },
    };

    /// <summary>A copy of <paramref name="config"/> with other settings, as the form sends it.</summary>
    private static ProviderConfig WithSettings(ProviderConfig config, Dictionary<string, string> changes, string? id = null)
    {
        var settings = new Dictionary<string, string>(config.Settings);
        foreach (var (key, value) in changes)
            settings[key] = value;
        return new ProviderConfig
        {
            Id = id ?? config.Id, Kind = config.Kind, ProviderType = config.ProviderType, DisplayName = config.DisplayName,
            Settings = settings, Secrets = config.Secrets,
        };
    }

    private async Task SeedAsync(params ProviderConfig[] configs)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderConfigStore>();
        foreach (var config in configs)
            await store.SaveProviderConfigAsync(config, CancellationToken.None);
    }

    private async Task<ProviderConfig?> StoredAsync(string id, ProviderKind kind)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IProviderConfigStore>().GetProviderConfigByIdAsync(id, kind, CancellationToken.None);
    }

    private async Task<ProviderConfig> MaskedAsync(string id, ProviderKind kind) =>
        (await Providers().GetProviderConfigsAsync(kind)).Single(c => c.Id == id);

    // ── Access ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AccessRole.ReadOnly)]
    [InlineData(AccessRole.Operator)]
    public async Task ProviderEndpoints_RefuseEveryoneButAdmin(AccessRole role)
    {
        var client = Http(TestUserTokens.Global(role));

        (await client.GetAsync("/api/ui/settings/provider-configs?kind=Issue")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/ui/settings/provider-configs/check", GitHubIssue())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/ui/settings/provider-configs/{IssueId}/agent-labels")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.DeleteAsync($"/api/ui/settings/provider-configs/{IssueId}?kind=Issue")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/ui/settings/models")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Reads ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Reads_MaskEveryCredential_AndKeepTheRestOfTheConfig()
    {
        await SeedAsync(GitHubIssue(), GitHubRepo(), GitLabPipeline());
        var admin = Http(TestUserTokens.Global(AccessRole.Admin));

        foreach (var kind in new[] { "Issue", "Repository", "Pipeline" })
        {
            var body = await admin.GetStringAsync($"/api/ui/settings/provider-configs?kind={kind}");
            body.Should().NotContain(KeyCanary).And.NotContain(TokenCanary).And.NotContain(AccessTokenCanary).And.NotContain(SecretCanary);
        }

        var repo = await MaskedAsync(RepoId, ProviderKind.Repository);
        repo.Settings[ProviderSettingKeys.PrivateKeyBase64].Should().Be(Mask);
        repo.Settings[ProviderSettingKeys.Token].Should().Be(Mask);
        repo.Secrets.Should().Equal(new Dictionary<string, string> { ["NPM_TOKEN"] = Mask });
        repo.Should().BeEquivalentTo(GitHubRepo(), o => o.Excluding(c => c.Settings).Excluding(c => c.Secrets));
        repo.Settings.Where(s => s.Value != Mask).Should().BeEquivalentTo(
            GitHubRepo().Settings.Where(s => s.Key is not (ProviderSettingKeys.PrivateKeyBase64 or ProviderSettingKeys.Token)));
        (await MaskedAsync(GitLabId, ProviderKind.Pipeline)).Settings[ProviderSettingKeys.AccessToken].Should().Be(Mask);
    }

    [Fact]
    public void TheMaskedCopy_HasEveryProviderConfigProperty()
    {
        typeof(ProviderConfig).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            [
                nameof(ProviderConfig.BlacklistedPaths), nameof(ProviderConfig.DisplayName), nameof(ProviderConfig.Id),
                nameof(ProviderConfig.Kind), nameof(ProviderConfig.ProviderType), nameof(ProviderConfig.RepositoryRole),
                nameof(ProviderConfig.RequiredLabels), nameof(ProviderConfig.Secrets), nameof(ProviderConfig.Settings),
                nameof(ProviderConfig.SetupSteps), nameof(ProviderConfig.SteeringContent),
            ],
            "UiSecretMask.Copy copies these; a new ProviderConfig property must be added there, or reads and saves drop it");
    }

    // ── Saves ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SavingTheMaskedForm_KeepsTheStoredCredentials_AndAppliesTheEdits()
    {
        await SeedAsync(GitHubRepo());
        var masked = await MaskedAsync(RepoId, ProviderKind.Repository);
        // Secrets were in the old-format seeded provider; they appear in the masked form.
        // The UI save path strips them because they now live on the pipeline job template (issue #3575).
        var secrets = new Dictionary<string, string>(masked.Secrets!) { ["NEW_KEY"] = "new-value" };
        var edited = UiSecretMask.Copy(masked, new Dictionary<string, string>(masked.Settings) { [ProviderSettingKeys.Repo] = "renamed" }, secrets);

        await Providers().SaveProviderConfigAsync(edited);

        var stored = (await StoredAsync(RepoId, ProviderKind.Repository))!;
        stored.Settings[ProviderSettingKeys.PrivateKeyBase64].Should().Be(KeyCanary);
        stored.Settings[ProviderSettingKeys.Token].Should().Be(TokenCanary);
        stored.Settings[ProviderSettingKeys.Repo].Should().Be("renamed");
        // Secrets and SteeringContent no longer live on the repository provider (issue #3575);
        // the UI save path silently strips them from a pre-migration provider as part of migration.
        stored.Secrets.Should().BeNullOrEmpty("repository provider no longer stores Secrets — they live on the pipeline job template");
        stored.SteeringContent.Should().BeNullOrWhiteSpace("repository provider no longer stores SteeringContent — it lives on the pipeline job template");
    }

    [Fact]
    public async Task SavingAMask_WithAChangedApiUrl_IsRefused()
    {
        await SeedAsync(GitHubIssue());
        var masked = await MaskedAsync(IssueId, ProviderKind.Issue);

        var save = () => Providers().SaveProviderConfigAsync(
            WithSettings(masked, new() { [ProviderSettingKeys.ApiUrl] = "https://attacker.example.com" }));

        (await save.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("The API URL changed");
        (await StoredAsync(IssueId, ProviderKind.Issue))!.Settings[ProviderSettingKeys.ApiUrl].Should().Be(ProviderSettingKeys.DefaultApiUrl);
    }

    [Fact]
    public async Task SavingAMask_ForANewProvider_IsRefused()
    {
        var save = () => Providers().SaveProviderConfigAsync(
            WithSettings(GitHubIssue(), new() { [ProviderSettingKeys.PrivateKeyBase64] = Mask }, id: NewId));

        await save.Should().ThrowAsync<InvalidOperationException>();
        (await StoredAsync(NewId, ProviderKind.Issue)).Should().BeNull();
    }

    [Fact]
    public async Task ACopy_TakesTheSourceProvidersCredentials()
    {
        await SeedAsync(GitHubIssue());
        var masked = await MaskedAsync(IssueId, ProviderKind.Issue);
        var related = new ProviderConfig
        {
            Id = NewId, Kind = ProviderKind.Pipeline, ProviderType = ProviderTypes.GitHub, DisplayName = "CI",
            Settings = new Dictionary<string, string>(masked.Settings),
        };

        try
        {
            await Providers().CopyProviderConfigAsync(related, IssueId, ProviderKind.Issue);

            (await StoredAsync(NewId, ProviderKind.Pipeline))!.Settings[ProviderSettingKeys.PrivateKeyBase64].Should().Be(KeyCanary);
        }
        finally
        {
            await Providers().DeleteProviderConfigAsync(NewId, ProviderKind.Pipeline);
        }

        (await StoredAsync(NewId, ProviderKind.Pipeline)).Should().BeNull();
    }

    // ── Checks ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Check_GitHub_UsesTheStoredKeyForAMask()
    {
        await SeedAsync(GitHubIssue());
        var masked = await MaskedAsync(IssueId, ProviderKind.Issue);

        var check = await Providers().CheckAsync(masked);

        check.Should().Be(new UiProviderCheck(true, "✅ acme/webapp — permissions: read, write"));
        factory.GitHub.Keys.Last().Should().Be(KeyCanary);
    }

    [Fact]
    public async Task Check_GitHub_ReportsAFailedCheck_AndABadInstallationId()
    {
        var bad = await Providers().CheckAsync(
            WithSettings(GitHubIssue(), new() { [ProviderSettingKeys.PrivateKeyBase64] = RecordingGitHubValidation.BadKey }, id: NewId));
        var noInstallation = await Providers().CheckAsync(
            WithSettings(GitHubIssue(), new() { [ProviderSettingKeys.InstallationId] = "abc" }, id: NewId));

        bad.Should().Be(new UiProviderCheck(false, "Invalid private key: could not decode from base64"));
        noInstallation.Should().Be(new UiProviderCheck(false, "Installation ID must be a valid number."));
    }

    [Fact]
    public async Task Check_GitLab_UsesTheStoredTokenForAMask()
    {
        await SeedAsync(GitLabPipeline());
        var masked = await MaskedAsync(GitLabId, ProviderKind.Pipeline);

        var check = await Providers().CheckAsync(masked);

        check.Should().Be(new UiProviderCheck(true, "Connected to group/project-42 (access: Maintainer)"));
        factory.GitLab.Tokens.Last().Should().Be(AccessTokenCanary);
    }

    [Fact]
    public async Task Check_AMaskWithAChangedApiUrl_IsRefused_BeforeAnythingIsCalled()
    {
        await SeedAsync(GitLabPipeline());
        var masked = await MaskedAsync(GitLabId, ProviderKind.Pipeline);
        var calls = factory.GitLab.Tokens.Count;

        var check = () => Providers().CheckAsync(WithSettings(masked, new() { [ProviderSettingKeys.ApiUrl] = "https://attacker.example.com" }));

        (await check.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("The API URL changed");
        factory.GitLab.Tokens.Should().HaveCount(calls);
    }

    [Fact]
    public async Task Check_AStoredConfigWithoutAnApiUrl_AcceptsTheDefaultUrlTheFormFillsIn()
    {
        // A config created through the service surface may leave the URL out; the form shows the default.
        var seeded = GitHubIssue();
        seeded.Settings.Remove(ProviderSettingKeys.ApiUrl);
        await SeedAsync(seeded);
        var masked = await MaskedAsync(IssueId, ProviderKind.Issue);

        var check = await Providers().CheckAsync(WithSettings(masked, new() { [ProviderSettingKeys.ApiUrl] = ProviderSettingKeys.DefaultApiUrl + "/" }));

        check.Success.Should().BeTrue();
        factory.GitHub.Keys.Last().Should().Be(KeyCanary);
    }

    [Fact]
    public async Task Check_AProviderTypeWithoutACheck_IsRefused()
    {
        var kiro = new ProviderConfig { Id = NewId, Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Kiro" };

        var check = () => Providers().CheckAsync(kiro);

        (await check.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("no credential check for KiroCli");
    }

    [Fact]
    public async Task ActionsAccess_UsesTheStoredKeyForAMask()
    {
        await SeedAsync(GitHubIssue());
        var masked = await MaskedAsync(IssueId, ProviderKind.Issue);

        var check = await Providers().CheckActionsAccessAsync(masked);

        check.Success.Should().BeTrue();
        factory.GitHub.Keys.Last().Should().Be(KeyCanary);
    }

    [Fact]
    public async Task Repositories_ListsTheInstallationsRepositories_OrRefusesWithTheReason()
    {
        await SeedAsync(GitHubIssue());
        var masked = await MaskedAsync(IssueId, ProviderKind.Issue);

        var repositories = await Providers().ListRepositoriesAsync(masked);
        var refused = () => Providers().ListRepositoriesAsync(
            WithSettings(GitHubIssue(), new() { [ProviderSettingKeys.PrivateKeyBase64] = RecordingGitHubValidation.BadKey }, id: NewId));

        repositories.Should().Equal(new UiRepository("acme/webapp", "acme", "webapp"), new UiRepository("acme/api", "acme", "api"));
        factory.GitHub.Keys.Should().Contain(KeyCanary);
        (await refused.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Invalid private key");
    }

    // ── Issue provider operations ───────────────────────────────────────

    private Mock<IIssueProvider> IssueProviderFor(string key)
    {
        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        factory.Providers
            .Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Settings[ProviderSettingKeys.PrivateKeyBase64] == key)))
            .Returns(provider.Object);
        return provider;
    }

    [Fact]
    public async Task Initialize_RunsOnTheStoredCredentials_AndReportsMissingLabels()
    {
        await SeedAsync(GitHubIssue());
        var provider = IssueProviderFor(KeyCanary);
        provider.SetupSequence(p => p.InitializeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true).ReturnsAsync(false);
        var masked = await MaskedAsync(IssueId, ProviderKind.Issue);

        (await Providers().InitializeAsync(masked)).Should().Be(new UiProviderCheck(true, "Repository initialized successfully."));
        (await Providers().InitializeAsync(masked)).Should().Be(new UiProviderCheck(false, "Credentials validated but some labels could not be created."));
    }

    [Fact]
    public async Task Initialize_AProviderFailure_IsRefusedWithItsMessage()
    {
        IssueProviderFor("init-failure-key").Setup(p => p.InitializeAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Bad credentials"));

        var initialize = () => Providers().InitializeAsync(
            WithSettings(GitHubIssue(), new() { [ProviderSettingKeys.PrivateKeyBase64] = "init-failure-key" }, id: NewId));

        (await initialize.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("Bad credentials");
    }

    [Fact]
    public async Task AgentLabels_AreCheckedAndCreated_OnTheStoredIssueProvider()
    {
        await SeedAsync(GitHubIssue());
        var provider = IssueProviderFor(KeyCanary);
        provider.Setup(p => p.HasAgentLabelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        provider.Setup(p => p.EnsureAgentLabelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        (await Providers().HasAgentLabelsAsync(IssueId)).Should().BeFalse();
        (await Providers().EnsureAgentLabelsAsync(IssueId)).Should().BeTrue();
        var unknown = () => Providers().HasAgentLabelsAsync(NewId);
        await unknown.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Models ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Models_AFailedFetch_ReturnsTheReasonAsText()
    {
        // The test host has no model-fetch Job template and no agent key, so the fetch fails at once.
        var (models, error) = await Providers().GetModelsAsync();

        models.Should().BeEmpty();
        error.Should().NotBeNullOrWhiteSpace().And.NotStartWith("{");
    }
}
