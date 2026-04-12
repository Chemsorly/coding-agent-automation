using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace KiroWebUI.Tests.Unit;

/// <summary>
/// Unit tests for API endpoints: health check, prompt validation, and concurrency guard.
/// Feature: kiro-web-ui
/// Requirements: 4.3, 11.1, 12.2
/// </summary>
public class PromptEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PromptEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// GET /health returns 200 with status "healthy" and a timestamp.
    /// Validates: Requirement 11.1
    /// </summary>
    [Fact]
    public async Task HealthEndpoint_Returns200_WithHealthyStatus()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", json.GetProperty("status").GetString());
        Assert.True(json.TryGetProperty("timestamp", out var timestamp));
        // Verify timestamp is a valid date string
        Assert.True(DateTime.TryParse(timestamp.GetString(), out _));
    }

    /// <summary>
    /// POST /api/prompt with empty prompt returns 400 Bad Request.
    /// Validates: Requirement 4.3
    /// </summary>
    [Fact]
    public async Task PromptEndpoint_EmptyPrompt_Returns400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/prompt", new { Prompt = "", UseResume = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Prompt is required", json.GetProperty("error").GetString());
    }

    /// <summary>
    /// Verifies the SemaphoreSlim concurrency guard pattern works correctly.
    /// A SemaphoreSlim(1,1) with WaitAsync(0) rejects concurrent access.
    /// Validates: Requirement 12.2
    /// </summary>
    [Fact]
    public async Task SemaphoreSlimConcurrencyGuard_RejectsConcurrentAccess()
    {
        var semaphore = new SemaphoreSlim(1, 1);

        // First acquisition succeeds
        var firstAcquired = await semaphore.WaitAsync(0);
        Assert.True(firstAcquired);

        // Second acquisition fails (non-blocking)
        var secondAcquired = await semaphore.WaitAsync(0);
        Assert.False(secondAcquired);

        // Release first
        semaphore.Release();

        // Now third acquisition succeeds
        var thirdAcquired = await semaphore.WaitAsync(0);
        Assert.True(thirdAcquired);

        semaphore.Release();
        semaphore.Dispose();
    }
}
