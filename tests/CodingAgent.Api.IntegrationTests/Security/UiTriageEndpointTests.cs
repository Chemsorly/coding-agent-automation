using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// Spec 051 2.7: the Triage pages on the UI surface. Reading needs read on the triage's project (a list without
/// one needs global read), acting needs operator on it, and a triage of a project the user cannot see answers 404.
/// The API records the acting user from the token, not from the body.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiTriageEndpointTests(ApiWebApplicationFactory factory)
{
    private const string ProjectT = "6f1c2a9e-0000-0000-0000-0000000000f7";
    private const string ProjectU = "6f1c2a9e-0000-0000-0000-0000000000f8";

    private HttpClient Http(AccessGrant grant, string username = "tester")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant, $"oidc:{username}", username));
        return client;
    }

    /// <summary>A finished operator triage of <paramref name="projectId"/>, straight in the store.</summary>
    private async Task<Guid> SeedTriageAsync(string projectId)
    {
        var id = Guid.NewGuid();
        await factory.Services.GetRequiredService<ITriageStore>().CreateAsync(new TriageRecord
        {
            Id = id,
            ProjectId = projectId,
            Source = TriageSource.Operator,
            Title = "Checkout returns 502",
            RequestedBy = "anna",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }

    [Fact]
    public async Task ServiceKeys_AreRefused()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);

        (await client.GetAsync("/api/ui/triages")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_WithoutAProject_NeedsGlobalRead()
    {
        (await Http(TestUserTokens.Scoped(ProjectT, AccessRole.Operator)).GetAsync("/api/ui/triages"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Http(TestUserTokens.Global(AccessRole.ReadOnly)).GetAsync("/api/ui/triages"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task List_OfAProject_NeedsReadOnIt()
    {
        var reader = Http(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly));

        (await reader.GetAsync($"/api/ui/triages?projectId={ProjectT}&tab=need_you")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.GetAsync($"/api/ui/triages?projectId={ProjectU}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_IsCheckedAgainstTheStoredTriagesProject()
    {
        var id = await SeedTriageAsync(ProjectT);

        var detail = await Http(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly))
            .GetFromJsonAsync<TriageDetail>($"/api/ui/triages/{id}", PipelineJsonOptions.Default);
        detail!.Record.Title.Should().Be("Checkout returns 502");

        (await Http(TestUserTokens.Scoped(ProjectU, AccessRole.Operator)).GetAsync($"/api/ui/triages/{id}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "the triage's project is not the user's");
        (await Http(TestUserTokens.Global(AccessRole.ReadOnly)).GetAsync($"/api/ui/triages/{Guid.NewGuid()}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Acting_NeedsOperatorOnTheTriagesProject()
    {
        var id = await SeedTriageAsync(ProjectT);
        var body = new DismissTriageRequest { By = "anyone" };

        (await Http(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly)).PostAsJsonAsync($"/api/ui/triages/{id}/dismiss", body, PipelineJsonOptions.Default))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "a reader of the project may see the triage but not act on it");
        (await Http(TestUserTokens.Scoped(ProjectU, AccessRole.Operator)).PostAsJsonAsync($"/api/ui/triages/{id}/dismiss", body, PipelineJsonOptions.Default))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "an operator of another project cannot see the triage");
    }

    [Fact]
    public async Task Dismiss_RecordsTheUserFromTheToken_NotFromTheBody()
    {
        var id = await SeedTriageAsync(ProjectT);

        var response = await Http(TestUserTokens.Scoped(ProjectT, AccessRole.Operator), "bert")
            .PostAsJsonAsync($"/api/ui/triages/{id}/dismiss", new DismissTriageRequest { By = "spoofed", Reason = "duplicate" }, PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<TriageDetail>(PipelineJsonOptions.Default);
        detail!.Record.DismissedBy.Should().Be("bert");
        detail.Record.State.Should().Be(TriageState.Dismissed);
    }

    [Fact]
    public async Task Create_NeedsOperatorOnTheChosenProject_AndRefusesAnUnknownOneWithTheReason()
    {
        var request = new CreateTriageRequest
        {
            ProjectId = ProjectT,
            RequestedBy = "spoofed",
            Request = new TriageRequest { Title = "t", WhatHappened = "w", Expected = "e" },
        };

        (await Http(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly)).PostAsJsonAsync("/api/ui/triages", request, PipelineJsonOptions.Default))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var response = await Http(TestUserTokens.Global(AccessRole.Operator))
            .PostAsJsonAsync("/api/ui/triages", request with { ProjectId = Guid.NewGuid().ToString() }, PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<string>()).Should().NotBeNullOrWhiteSpace("the page shows the API's reason");
    }

    [Fact]
    public async Task Preview_NeedsOperator_AndDescribesTheProject()
    {
        var projects = factory.Services.GetRequiredService<IProjectStore>();
        await projects.SaveProjectAsync(new PipelineProject { Id = ProjectT, Name = "Triage T" }, CancellationToken.None);
        await projects.SaveTemplateAsync(ProjectT, new PipelineJobTemplate
        {
            Id = "7a000000-0000-0000-0000-0000000000f7", Name = "t-web", IssueProviderId = "ip-t", RepoProviderId = "rp-t", Enabled = true,
        }, CancellationToken.None);

        (await Http(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly)).GetAsync($"/api/ui/triages/preview?projectId={ProjectT}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var preview = await Http(TestUserTokens.Scoped(ProjectT, AccessRole.Operator))
            .GetFromJsonAsync<TriagePreview>($"/api/ui/triages/preview?projectId={ProjectT}");
        preview!.Repositories.Should().ContainSingle(r => r.Name == "t-web" && r.IsExecutor);
    }
}
