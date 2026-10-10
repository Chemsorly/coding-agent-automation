using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.IntegrationTests.Triage;

/// <summary>
/// The triage endpoints through the real host: the operator tier only, and the problem responses the web UI
/// shows to the user.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class TriageEndpointTests
{
    private readonly ApiWebApplicationFactory _factory;

    public TriageEndpointTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Operator()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
        return client;
    }

    [Theory]
    [InlineData("GET", "/api/triages")]
    [InlineData("POST", "/api/triages")]
    [InlineData("POST", "/api/triages/00000000-0000-0000-0000-000000000001/create-issues")]
    [InlineData("POST", "/api/triages/00000000-0000-0000-0000-000000000001/rerun")]
    public async Task AgentDerivedKeys_AreRefused(string method, string path)
    {
        const string agentId = "triage-probe";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ApiWebApplicationFactory.ApiKey));
        var derivedKey = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(agentId))).ToLowerInvariant();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", derivedKey);

        using var request = new HttpRequestMessage(new HttpMethod(method), $"{path}?agentId={agentId}")
        {
            Content = method == "POST" ? JsonContent.Create(new { }) : null,
        };
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "an agent must never start triages or create issues from drafts");
    }

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        using var client = _factory.CreateClient();

        (await client.GetAsync("/api/triages")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_ForAProjectWithoutTriages_IsEmpty()
    {
        using var client = Operator();

        var response = await client.GetAsync($"/api/triages?projectId={Guid.NewGuid()}&tab=need_you");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<TriageListPage>(PipelineJsonOptions.Default);
        page!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_UnknownTriage_Is404()
    {
        using var client = Operator();

        (await client.GetAsync($"/api/triages/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_ForAnUnknownProject_ReturnsAProblemWithTheReason()
    {
        using var client = Operator();

        var response = await client.PostAsJsonAsync("/api/triages", new CreateTriageRequest
        {
            ProjectId = Guid.NewGuid().ToString(),
            RequestedBy = "anna",
            Request = new TriageRequest { Title = "t", WhatHappened = "w", Expected = "e" },
        }, PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Project not found");
    }
}
