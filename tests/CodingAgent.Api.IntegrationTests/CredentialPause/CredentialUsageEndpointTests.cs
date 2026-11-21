using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Api.IntegrationTests.Security;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.CredentialPause;

/// <summary>
/// The usage thresholds of the Claude subscription in the UI: admins set them, everyone sees them with the last
/// readings, and a resume over a threshold sticks until the window resets.
/// </summary>
[Collection(CredentialPauseCollection.Name)]
public sealed class CredentialUsageEndpointTests : IClassFixture<CredentialPauseWebApplicationFactory>
{
    private const string Route = "/api/ui/agent-credentials";
    private const string Sub = AgentCredentials.ClaudeSubscription;
    private readonly CredentialPauseWebApplicationFactory _factory;

    public CredentialUsageEndpointTests(CredentialPauseWebApplicationFactory factory)
    {
        _factory = factory;
        factory.SeedProfilesAsync().GetAwaiter().GetResult();
        Pauses.ResumeAsync(Sub, "test", CancellationToken.None).GetAwaiter().GetResult();
        Usage.SaveThresholdsAsync(CredentialUsageThresholds.Default, CancellationToken.None).GetAwaiter().GetResult();
    }

    private CredentialPauseStore Pauses => _factory.Services.GetRequiredService<CredentialPauseStore>();
    private CredentialUsageStore Usage => _factory.Services.GetRequiredService<CredentialUsageStore>();

    private HttpClient Client(AccessRole role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestUserTokens.For(TestUserTokens.Global(role)));
        return client;
    }

    private Task<HttpResponseMessage> SaveAsync(AccessRole role, CredentialUsageThresholds thresholds) =>
        Client(role).PutAsJsonAsync($"{Route}/usage-thresholds", thresholds, PipelineJsonOptions.Default);

    private static IReadOnlyList<AgentRateLimitObservation> FiveHour(double utilization, DateTimeOffset resets) =>
        [new() { Provider = "claude", Window = CredentialUsageWindows.FiveHour, Status = "allowed", Utilization = utilization, ResetsAt = resets }];

    [Fact]
    public async Task Admin_SavesTheThresholds_AndEveryReaderSeesThemWithTheLastReading()
    {
        var resets = DateTimeOffset.UtcNow.AddHours(2);
        await Usage.RecordAsync(Sub, FiveHour(0.42, resets), CancellationToken.None);

        (await SaveAsync(AccessRole.Admin, new CredentialUsageThresholds { FiveHourPercent = 70, SevenDayPercent = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var page = await Client(AccessRole.ReadOnly).GetFromJsonAsync<UiAgentCredentialsPage>(Route, PipelineJsonOptions.Default);
        page!.UsageThresholds.Should().Be(new CredentialUsageThresholds { FiveHourPercent = 70, SevenDayPercent = 0 });
        var usage = page.Credentials.Single(c => c.Credential == Sub).Usage;
        usage.Should().ContainSingle();
        usage![0].Window.Should().Be(CredentialUsageWindows.FiveHour);
        usage[0].Utilization.Should().Be(0.42);
        usage[0].ThresholdPercent.Should().Be(70);
        page.Credentials.Single(c => c.Credential == AgentCredentials.Kiro).Usage.Should().BeNull();
    }

    [Fact]
    public async Task Operator_CannotChangeTheThresholds() =>
        (await SaveAsync(AccessRole.Operator, new CredentialUsageThresholds { FiveHourPercent = 50 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Thresholds_OverAHundredPercent_Are400() =>
        (await SaveAsync(AccessRole.Admin, new CredentialUsageThresholds { FiveHourPercent = 101 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Resume_OverTheThreshold_LetsTheWindowThroughUntilItResets()
    {
        var resets = DateTimeOffset.UtcNow.AddHours(2);
        await Usage.RecordAsync(Sub, FiveHour(0.8, resets), CancellationToken.None);

        (await Client(AccessRole.Operator).PostAsync($"{Route}/{Sub}/resume", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Usage.LoadAsync(Sub, CancellationToken.None)).ResumedThrough[CredentialUsageWindows.FiveHour].Should().Be(resets);
    }
}
