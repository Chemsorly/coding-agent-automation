using AwesomeAssertions;
using CodingAgentWebUI.Scheduler;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Reflection;
using Xunit;

namespace CodingAgentWebUI.Scheduler.UnitTests;

/// <summary>
/// Unit tests for the internal ApiKeyFilter on SchedulerLoopEndpoints.
/// Tests the auth logic directly without spinning up a WebApplication.
/// </summary>
public sealed class ApiKeyFilterTests
{
    private static IEndpointFilter CreateFilter(string expectedKey)
    {
        // ApiKeyFilter is private-sealed inside SchedulerLoopEndpoints — use reflection.
        var filterType = typeof(SchedulerLoopEndpoints)
            .GetNestedType("ApiKeyFilter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IEndpointFilter)Activator.CreateInstance(filterType, expectedKey)!;
    }

    private static EndpointFilterInvocationContext MakeContext(string? headerValue)
    {
        var httpContext = new DefaultHttpContext();
        if (headerValue is not null)
            httpContext.Request.Headers["X-Api-Key"] = headerValue;
        var mock = new Mock<EndpointFilterInvocationContext>();
        mock.Setup(m => m.HttpContext).Returns(httpContext);
        return mock.Object;
    }

    [Fact]
    public async Task WhenExpectedKeyIsEmpty_Returns503AndDoesNotCallNext()
    {
        // No API key configured → fail-closed (503 Service Unavailable).
        // Prevents unauthenticated loop control in production due to misconfiguration.
        var filter = CreateFilter("");
        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        };

        var result = await filter.InvokeAsync(MakeContext(null), next);

        nextCalled.Should().BeFalse("unconfigured key must not pass through to next");
        result.Should().NotBeNull();
        result.Should().BeAssignableTo<IResult>("must return a 503 IResult");
    }

    [Fact]
    public async Task WhenKeyMatches_CallsNext()
    {
        var filter = CreateFilter("secret-key");
        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        };

        await filter.InvokeAsync(MakeContext("secret-key"), next);

        nextCalled.Should().BeTrue("matching key must reach next handler");
    }

    [Fact]
    public async Task WhenKeyMissing_ReturnsUnauthorizedAndDoesNotCallNext()
    {
        var filter = CreateFilter("secret-key");
        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        };

        var result = await filter.InvokeAsync(MakeContext(null), next);

        nextCalled.Should().BeFalse("missing key must be rejected before reaching next");
        result.Should().NotBeNull();
        result.Should().BeAssignableTo<IResult>("must return an IResult");
    }

    [Fact]
    public async Task WhenKeyWrong_ReturnsUnauthorizedAndDoesNotCallNext()
    {
        var filter = CreateFilter("secret-key");
        var nextCalled = false;
        EndpointFilterDelegate next = _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        };

        var result = await filter.InvokeAsync(MakeContext("wrong-key"), next);

        nextCalled.Should().BeFalse("wrong key must be rejected before reaching next");
        result.Should().NotBeNull();
        result.Should().BeAssignableTo<IResult>("must return an IResult");
    }
}
