using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="RepositorySettings"/>.
/// Covers TemplateFor lookup logic and WithTemplateSettings projection.
/// </summary>
public class RepositorySettingsTests
{
    private const string RepoId = "repo-1";

    private static ProviderConfig MakeRepoConfig(
        string id = RepoId,
        IReadOnlyList<string>? requiredLabels = null,
        Dictionary<string, string>? secrets = null,
        IReadOnlyList<SetupStep>? setupSteps = null,
        string? steeringContent = null,
        IReadOnlyList<string>? blacklistedPaths = null) => new()
    {
        Id = id,
        Kind = ProviderKind.Repository,
        ProviderType = "GitHub",
        DisplayName = "Test Repo",
        Settings = new Dictionary<string, string> { ["token"] = "t" },
        RepositoryRole = RepositoryRole.Work,
        RequiredLabels = requiredLabels,
        Secrets = secrets,
        SetupSteps = setupSteps,
        SteeringContent = steeringContent,
        BlacklistedPaths = blacklistedPaths,
    };

    private static PipelineJobTemplate MakeTemplate(
        string repoProviderId = RepoId,
        bool enabled = true,
        string? id = null,
        IReadOnlyList<string>? requiredLabels = null,
        Dictionary<string, string>? secrets = null,
        IReadOnlyList<SetupStep>? setupSteps = null,
        string? steeringContent = null,
        IReadOnlyList<string>? blacklistedPaths = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString(),
        Name = "T",
        IssueProviderId = "ip-1",
        RepoProviderId = repoProviderId,
        Enabled = enabled,
        RequiredLabels = requiredLabels,
        Secrets = secrets,
        SetupSteps = setupSteps,
        SteeringContent = steeringContent,
        BlacklistedPaths = blacklistedPaths,
    };

    // ── TemplateFor ──────────────────────────────────────────────────────────

    [Fact]
    public void TemplateFor_PrefersEnabledTemplate_WhenBothEnabledAndDisabledExist()
    {
        var enabled = MakeTemplate(enabled: true, id: "enabled");
        var disabled = MakeTemplate(enabled: false, id: "disabled");

        var result = RepositorySettings.TemplateFor(RepoId, [disabled, enabled]);

        result.Should().NotBeNull();
        result!.Id.Should().Be("enabled");
    }

    [Fact]
    public void TemplateFor_FallsBackToDisabledTemplate_WhenNoneEnabled()
    {
        var disabled = MakeTemplate(enabled: false, id: "only-disabled");

        var result = RepositorySettings.TemplateFor(RepoId, [disabled]);

        result.Should().NotBeNull();
        result!.Id.Should().Be("only-disabled");
    }

    [Fact]
    public void TemplateFor_ReturnsNull_WhenNoTemplatesForRepo()
    {
        var otherTemplate = MakeTemplate(repoProviderId: "other-repo");

        var result = RepositorySettings.TemplateFor(RepoId, [otherTemplate]);

        result.Should().BeNull();
    }

    [Fact]
    public void TemplateFor_ReturnsNull_WhenListEmpty()
    {
        var result = RepositorySettings.TemplateFor(RepoId, []);

        result.Should().BeNull();
    }

    // ── WithTemplateSettings ────────────────────────────────────────────────

    [Fact]
    public void WithTemplateSettings_CopiesAllFiveFields_WhenTemplateHasAllSet()
    {
        var repoConfig = MakeRepoConfig();
        var template = MakeTemplate(
            requiredLabels: ["kiro", "dotnet"],
            secrets: new Dictionary<string, string> { ["KEY"] = "val" },
            setupSteps: [new SetupStep { Name = "restore", Command = "dotnet restore" }],
            steeringContent: "## Architecture\nFoo",
            blacklistedPaths: [".kiro", ".brain"]);

        var result = RepositorySettings.WithTemplateSettings(repoConfig, template);

        result.RequiredLabels.Should().BeEquivalentTo(["kiro", "dotnet"]);
        result.Secrets.Should().ContainKey("KEY").WhoseValue.Should().Be("val");
        result.SetupSteps.Should().HaveCount(1);
        result.SetupSteps![0].Name.Should().Be("restore");
        result.SteeringContent.Should().Be("## Architecture\nFoo");
        result.BlacklistedPaths.Should().BeEquivalentTo([".kiro", ".brain"]);
    }

    [Fact]
    public void WithTemplateSettings_NullTemplate_ClearsAllFiveFields()
    {
        var repoConfig = MakeRepoConfig(
            requiredLabels: ["kiro"],
            secrets: new Dictionary<string, string> { ["K"] = "v" },
            setupSteps: [new SetupStep { Name = "n", Command = "c" }],
            steeringContent: "steering",
            blacklistedPaths: [".kiro"]);

        var result = RepositorySettings.WithTemplateSettings(repoConfig, null);

        result.RequiredLabels.Should().BeNull();
        result.Secrets.Should().BeNull();
        result.SetupSteps.Should().BeNull();
        result.SteeringContent.Should().BeNull();
        result.BlacklistedPaths.Should().BeNull();
    }

    [Fact]
    public void WithTemplateSettings_EmptyTemplateLabels_ClearsRequiredLabels()
    {
        var repoConfig = MakeRepoConfig(requiredLabels: ["kiro"]);
        var template = MakeTemplate(requiredLabels: []); // empty list → treated as unset

        var result = RepositorySettings.WithTemplateSettings(repoConfig, template);

        result.RequiredLabels.Should().BeNull();
    }

    [Fact]
    public void WithTemplateSettings_DoesNotModifyOriginalConfig()
    {
        var repoConfig = MakeRepoConfig(requiredLabels: ["original"]);
        var template = MakeTemplate(requiredLabels: ["new"]);

        var result = RepositorySettings.WithTemplateSettings(repoConfig, template);

        repoConfig.RequiredLabels.Should().BeEquivalentTo(["original"], "original must be unchanged");
        result.RequiredLabels.Should().BeEquivalentTo(["new"]);
    }

    [Fact]
    public void WithTemplateSettings_PreservesOtherFields()
    {
        var settings = new Dictionary<string, string> { ["token"] = "secret123" };
        var repoConfig = new ProviderConfig
        {
            Id = "special-id",
            DisplayName = "My Repo",
            ProviderType = "GitLab",
            Kind = ProviderKind.Repository,
            RepositoryRole = RepositoryRole.Brain,
            Settings = settings,
        };
        var template = MakeTemplate(requiredLabels: ["kiro"]);

        var result = RepositorySettings.WithTemplateSettings(repoConfig, template);

        result.Id.Should().Be("special-id");
        result.DisplayName.Should().Be("My Repo");
        result.ProviderType.Should().Be("GitLab");
        result.Kind.Should().Be(ProviderKind.Repository);
        result.RepositoryRole.Should().Be(RepositoryRole.Brain);
        result.Settings.Should().ContainKey("token").WhoseValue.Should().Be("secret123");
    }
}
