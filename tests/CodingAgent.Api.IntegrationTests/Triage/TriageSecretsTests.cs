using AwesomeAssertions;
using CodingAgent.Api.Triage;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Api.IntegrationTests.Triage;

/// <summary>
/// <see cref="TriageSecrets"/>: the secret values triage text is masked with. Since #3575 a repository's secrets live on
/// its pipeline job template; the repository connection never stores them (spec 053 req 10.8).
/// </summary>
public sealed class TriageSecretsTests
{
    private const string TemplateSecret = "s3cr3t-value";
    private const string ProfileMcpPassword = "profile-mcp-password";

    private static readonly PipelineProject Project = new() { Id = Guid.NewGuid().ToString(), Name = "Shop" };

    private readonly Mock<IConfigurationStore> _config = new();

    public TriageSecretsTests()
    {
        _config.Setup(c => c.GetProjectByIdAsync(Project.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Project);
        _config.Setup(c => c.LoadTemplatesForProjectAsync(Project.Id, It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new PipelineJobTemplate
            {
                Id = "t-api", Name = "api", RepoProviderId = "rp-api", IssueProviderId = "tracker-1",
                Secrets = new Dictionary<string, string> { ["DB_PASSWORD"] = TemplateSecret },
            },
        ]);
        _config.Setup(c => c.LoadAgentProfilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new AgentProfile
            {
                Id = "p", DisplayName = "p", AgentProviderConfigId = "a", Enabled = true,
                McpServers = [new McpServerConfig { Name = "db", Env = new Dictionary<string, string> { ["DB_PASSWORD"] = ProfileMcpPassword } }],
            },
        ]);
    }

    [Fact]
    public async Task LoadAsync_ContainsTheTemplatesSecrets_Req10_8()
    {
        var secrets = await TriageSecrets.LoadAsync(_config.Object, Project.Id, CancellationToken.None);

        secrets.Should().Contain(new KeyValuePair<string, string>("repo:t-api:DB_PASSWORD", TemplateSecret));
    }

    [Fact]
    public async Task LoadAsync_DoesNotReadTheRepositoryConnection_Req10_8()
    {
        await TriageSecrets.LoadAsync(_config.Object, Project.Id, CancellationToken.None);

        _config.Verify(
            c => c.GetProviderConfigByIdAsync(It.IsAny<string>(), ProviderKind.Repository, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LoadAsync_StillContainsTheProfilesMcpSecrets_Req10_6()
    {
        var secrets = await TriageSecrets.LoadAsync(_config.Object, Project.Id, CancellationToken.None);

        secrets.Select(s => s.Value).Should().Contain(ProfileMcpPassword);
    }
}
