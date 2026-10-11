using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>Tests for <see cref="PipelineApiTriageClient"/>: paths, query strings, 404s and API refusals.</summary>
public sealed class PipelineApiTriageClientTests
{
    private static readonly Guid TriageId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly StubHandler _handler = new();
    private readonly PipelineApiTriageClient _client;

    public PipelineApiTriageClientTests()
    {
        _client = new PipelineApiTriageClient(new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") });
    }

    [Fact]
    public async Task List_SendsTheFilters_AndReadsThePage()
    {
        _handler.Respond = _ => Json(new TriageListPage { Total = 3 });

        var page = await _client.ListAsync(new TriageListQuery
        {
            ProjectId = "p 1",
            Tab = TriageListTab.NeedYou,
            Source = TriageSource.Issue,
            Search = "50% off",
            Page = 2,
            PageSize = 10,
        });

        page.Total.Should().Be(3);
        var query = _handler.LastRequest!.RequestUri!.Query;
        query.Should().Contain($"tab={Name(TriageListTab.NeedYou)}")
            .And.Contain("page=2")
            .And.Contain("pageSize=10")
            .And.Contain("projectId=p%201")
            .And.Contain($"source={Name(TriageSource.Issue)}")
            .And.Contain("q=50%25%20off");
    }

    [Fact]
    public async Task List_WithoutFilters_SendsOnlyTabAndPaging()
    {
        _handler.Respond = _ => NullJson();

        var page = await _client.ListAsync(new TriageListQuery { Search = "  " });

        page.Items.Should().BeEmpty();
        var query = _handler.LastRequest!.RequestUri!.Query;
        query.Should().NotContain("projectId").And.NotContain("source").And.NotContain("q=");
    }

    [Fact]
    public async Task Get_ReturnsTheDetail_OrNullWhenNotFound()
    {
        _handler.Respond = _ => Json(Detail());
        var found = await _client.GetAsync(TriageId);
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be($"/api/triages/{TriageId:D}");

        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var missing = await _client.GetAsync(TriageId);

        found!.Record.Id.Should().Be(TriageId);
        missing.Should().BeNull();
    }

    [Fact]
    public async Task Get_ServerError_Throws()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var act = () => _client.GetAsync(TriageId);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task FindByRun_ReturnsTheTriageId()
    {
        var runId = Guid.NewGuid();
        _handler.Respond = _ => Json(new { id = TriageId });

        var id = await _client.FindByRunAsync(runId.ToString());

        id.Should().Be(TriageId);
        _handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be($"/api/triages/by-run/{runId:D}");
    }

    [Fact]
    public async Task FindByRun_NotFoundOrNoGuid_IsNull()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        (await _client.FindByRunAsync(Guid.NewGuid().ToString())).Should().BeNull();

        _handler.LastRequest = null;
        (await _client.FindByRunAsync("not-a-guid")).Should().BeNull();
        _handler.LastRequest.Should().BeNull("a run id that is no GUID is never sent");
    }

    [Fact]
    public async Task FindSimilar_EscapesTheParameters()
    {
        _handler.Respond = _ => NullJson();

        var items = await _client.FindSimilarAsync("p 1", "a&b");

        items.Should().BeEmpty();
        _handler.LastRequest!.RequestUri!.Query.Should().Be("?projectId=p%201&title=a%26b");
    }

    [Fact]
    public async Task Writes_UseTheirMethodsAndPaths()
    {
        _handler.Respond = _ => Json(Detail());
        var calls = new List<string>();
        _handler.OnRequest = r => calls.Add($"{r.Method} {r.RequestUri!.AbsolutePath}");

        await _client.CreateAsync(new CreateTriageRequest
        {
            ProjectId = "p",
            RequestedBy = "anna",
            Request = new TriageRequest { Title = "t", WhatHappened = "w", Expected = "e" },
        });
        await _client.RerunAsync(TriageId, new RerunTriageRequest { Author = "anna", Feedback = "f" });
        await _client.UpdateDraftAsync(TriageId, "a1 d1", new UpdateTriageDraftRequest
        {
            Kind = TriageDraftKind.RootFix, TargetRepository = "api", Title = "t", Body = "b", EditedBy = "anna",
        });
        await _client.ResetDraftAsync(TriageId, "a1-d1");
        await _client.DismissAsync(TriageId, new DismissTriageRequest { By = "anna" });
        _handler.Respond = _ => Json(new CreateTriageIssuesResult());
        await _client.CreateIssuesAsync(TriageId, new CreateTriageIssuesRequest { DraftIds = ["a1-d1"], CreatedBy = "anna" });

        var t = $"/api/triages/{TriageId:D}";
        calls.Should().Equal(
            "POST /api/triages",
            $"POST {t}/rerun",
            $"PUT {t}/drafts/a1%20d1",
            $"POST {t}/drafts/a1-d1/reset",
            $"POST {t}/dismiss",
            $"POST {t}/create-issues");
    }

    [Fact]
    public async Task Write_Refused_ThrowsWithTheProblemDetail()
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"detail\":\"An attempt is running\"}", Encoding.UTF8, "application/problem+json"),
        };

        var act = () => _client.DismissAsync(TriageId, new DismissTriageRequest { By = "anna" });

        var ex = (await act.Should().ThrowAsync<TriageApiException>()).Which;
        ex.StatusCode.Should().Be(409);
        ex.Message.Should().Be("An attempt is running");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"title\":\"Bad Request\"}")]
    [InlineData("{\"detail\":\"\"}")]
    public async Task Write_RefusedWithoutADetail_ThrowsAGenericMessage(string body)
    {
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(body) };

        var act = () => _client.ResetDraftAsync(TriageId, "d1");

        (await act.Should().ThrowAsync<TriageApiException>()).Which.Message.Should().Be("The request failed (400)");
    }

    [Fact]
    public async Task Write_EmptyAnswer_Throws()
    {
        _handler.Respond = _ => NullJson();

        var act = () => _client.ResetDraftAsync(TriageId, "d1");

        (await act.Should().ThrowAsync<TriageApiException>()).Which.Message.Should().Be("The API returned no content");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Name<T>(T value) where T : struct, Enum =>
        JsonSerializer.Serialize(value, PipelineJsonOptions.Default).Trim('"');

    private static TriageDetail Detail() => new()
    {
        Record = new TriageRecord
        {
            Id = TriageId,
            ProjectId = "p",
            Source = TriageSource.Operator,
            Title = "t",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        },
        Status = TriageStatus.NeedsReview,
    };

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, PipelineJsonOptions.Default), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage NullJson() =>
        new(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }
        public Action<HttpRequestMessage>? OnRequest { get; set; }
        public HttpRequestMessage? LastRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            OnRequest?.Invoke(request);
            return Task.FromResult(Respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
