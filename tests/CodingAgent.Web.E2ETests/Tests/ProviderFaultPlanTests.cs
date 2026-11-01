using System.Net;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;

namespace CodingAgent.Web.E2ETests.Tests;

[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class ProviderFaultPlanTests
{
    [Fact]
    public async Task FailNext_FailsOnlyTheNamedMethod()
    {
        var provider = new InMemoryIssueProvider();
        provider.Issues.Add(new IssueDetail { Identifier = "1", Title = "t", Description = "d", Labels = [] });
        provider.Faults.FailNext(nameof(IIssueProvider.CreateIssueAsync));

        var issue = await provider.GetIssueAsync("1", CancellationToken.None);
        Assert.Equal("1", issue.Identifier);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.CreateIssueAsync("t", "b", null, CancellationToken.None));
    }

    [Fact]
    public async Task FailNext_WithSkip_LetsEarlierCallsPass()
    {
        var provider = new InMemoryIssueProvider();
        provider.Faults.FailNext(nameof(IIssueProvider.CreateIssueAsync), skip: 1);

        // Call 1: skip=1 → succeeds
        await provider.CreateIssueAsync("t", "b", null, CancellationToken.None);
        // Call 2: skip exhausted → throws
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.CreateIssueAsync("t", "b", null, CancellationToken.None));
        // Call 3: times exhausted, entry removed → succeeds
        await provider.CreateIssueAsync("t", "b", null, CancellationToken.None);

        Assert.Equal(2, provider.CreatedIssues.Count);
    }

    [Fact]
    public async Task FailNext_WithStatus_CarriesStatusCode()
    {
        var provider = new InMemoryIssueProvider();
        provider.Faults.FailNext(nameof(IIssueProvider.CreateIssueAsync), HttpStatusCode.TooManyRequests);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.CreateIssueAsync("t", "b", null, CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task FailNext_WithException_ThrowsThatException()
    {
        var provider = new InMemoryIssueProvider();
        provider.Faults.FailNext(nameof(IIssueProvider.CreateIssueAsync), new InvalidOperationException("boom"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateIssueAsync("t", "b", null, CancellationToken.None));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Reset_ClearsPlannedFaults()
    {
        var provider = new InMemoryIssueProvider();
        provider.Faults.FailNext(nameof(IIssueProvider.CreateIssueAsync));
        provider.Faults.Reset();

        // Must not throw after reset
        await provider.CreateIssueAsync("t", "b", null, CancellationToken.None);
    }

    [Fact]
    public async Task ShouldFail_StillFailsEveryCreate()
    {
        var provider = new InMemoryIssueProvider();
        provider.ShouldFail = true;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.CreateIssueAsync("t", "b", null, CancellationToken.None));
    }

    [Fact]
    public async Task FailNext_WithTimes_FiresExactlyThatManyTimes()
    {
        var provider = new InMemoryIssueProvider();
        provider.Faults.FailNext(nameof(IIssueProvider.CreateIssueAsync), times: 2);

        // Calls 1 and 2 throw
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.CreateIssueAsync("t", "b", null, CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.CreateIssueAsync("t", "b", null, CancellationToken.None));
        // Call 3 succeeds — entry is removed after times is exhausted
        await provider.CreateIssueAsync("t", "b", null, CancellationToken.None);
        Assert.Single(provider.CreatedIssues);
    }

    [Fact]
    public async Task FailNext_ReplacesEarlierPlanForSameMethod()
    {
        var provider = new InMemoryIssueProvider();
        // First plan: throw InternalServerError
        provider.Faults.FailNext(nameof(IIssueProvider.CreateIssueAsync), HttpStatusCode.InternalServerError);
        // Replace with a different status code
        provider.Faults.FailNext(nameof(IIssueProvider.CreateIssueAsync), HttpStatusCode.TooManyRequests);

        // Only the second plan should fire
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.CreateIssueAsync("t", "b", null, CancellationToken.None));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        // No more faults — call succeeds
        await provider.CreateIssueAsync("t", "b", null, CancellationToken.None);
    }
}
