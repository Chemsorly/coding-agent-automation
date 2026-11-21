using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.CredentialPause;

/// <summary>Its own database: an import that fails half-way on InMemory (no transactions) leaves rows behind.</summary>
public sealed class UsageThresholdImportFactory : ApiWebApplicationFactory;

/// <summary>
/// The config bundle carries the usage thresholds of the Claude subscription; an import restores them, a bundle without
/// them leaves the defaults, and a bundle with invalid ones is refused before anything is cleared.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UsageThresholdImportTests : IClassFixture<UsageThresholdImportFactory>
{
    private static readonly JsonSerializerOptions BundleOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly CredentialUsageThresholds Custom = new() { FiveHourPercent = 60, SevenDayPercent = 0 };

    private readonly UsageThresholdImportFactory _factory;
    private readonly HttpClient _operator;

    public UsageThresholdImportTests(UsageThresholdImportFactory factory)
    {
        _factory = factory;
        _operator = factory.CreateClient();
        _operator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    private CredentialUsageStore Store => _factory.Services.GetRequiredService<CredentialUsageStore>();

    private async Task<HttpResponseMessage> ImportAsync(string bundleJson)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(bundleJson));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "bundle.json");
        return await _operator.PostAsync("/api/config/import", content);
    }

    private Task<HttpResponseMessage> ImportAsync(ConfigBundle bundle) => ImportAsync(JsonSerializer.Serialize(bundle, BundleOptions));

    [Fact]
    public async Task ExportThenImport_RestoresTheThresholds()
    {
        await Store.SaveThresholdsAsync(Custom, CancellationToken.None);

        var exported = await _operator.GetStringAsync("/api/config/export");
        JsonElement.Parse(exported).GetProperty("usageThresholds").GetString().Should().NotBeNullOrEmpty();
        await Store.SaveThresholdsAsync(CredentialUsageThresholds.Default, CancellationToken.None);

        (await ImportAsync(exported)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Store.LoadThresholdsAsync(CancellationToken.None)).Should().Be(Custom);
    }

    [Fact]
    public async Task ABundleWithoutThresholds_LeavesTheDefaults()
    {
        await Store.SaveThresholdsAsync(Custom, CancellationToken.None);

        (await ImportAsync(new ConfigBundle())).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Store.LoadThresholdsAsync(CancellationToken.None)).Should().Be(CredentialUsageThresholds.Default);
    }

    [Theory]
    [InlineData("""{"FiveHourPercent":101}""", "The 5-hour threshold must be between 0 and 100 %.")]
    [InlineData("{not json", "cannot be read")]
    public async Task ABundleWithInvalidThresholds_Is400_AndChangesNothing(string thresholds, string reason)
    {
        await Store.SaveThresholdsAsync(Custom, CancellationToken.None);

        var response = await ImportAsync(new ConfigBundle { UsageThresholds = thresholds });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Invalid usage thresholds").And.Contain(reason);
        (await Store.LoadThresholdsAsync(CancellationToken.None)).Should().Be(Custom, "the bundle was refused before anything was cleared");
    }
}
