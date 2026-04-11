using System.Net;
using System.Net.Http.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace KiroCliPoc.Tests.Properties;

/// <summary>
/// Property 3: API Empty/Null Prompt Rejection
/// For any PromptRequest where the Prompt field is null, empty, or composed entirely of whitespace,
/// the POST /api/prompt endpoint SHALL return HTTP 400 Bad Request without invoking the orchestrator.
/// Feature: kiro-web-ui
/// Validates: Requirements 4.3
/// </summary>
public class ApiPromptValidationPropertyTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiPromptValidationPropertyTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    public static class Generators
    {
        public static Arbitrary<string> WhitespaceOrEmptyString()
        {
            var whitespaceChars = new[] { ' ', '\t', '\n', '\r' };
            var emptyGen = Gen.Constant(string.Empty);
            var whitespaceGen = Gen.Choose(1, 20)
                .SelectMany(len =>
                    Gen.Elements(whitespaceChars).ArrayOf(len)
                        .Select(chars => new string(chars)));
            return Gen.OneOf(emptyGen, whitespaceGen).ToArbitrary();
        }
    }

    /// <summary>
    /// Property 3: Any empty or whitespace-only prompt submitted via POST /api/prompt
    /// returns HTTP 400 Bad Request.
    /// </summary>
    [Property(MaxTest = 50, Arbitrary = new[] { typeof(Generators) })]
    public void EmptyOrWhitespacePrompt_Returns400BadRequest(string invalidPrompt)
    {
        var client = _factory.CreateClient();

        var response = client.PostAsJsonAsync("/api/prompt", new { Prompt = invalidPrompt, UseResume = false })
            .GetAwaiter().GetResult();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Null prompt submitted via POST /api/prompt returns HTTP 400 Bad Request.
    /// </summary>
    [Fact]
    public async Task NullPrompt_Returns400BadRequest()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/prompt", new { Prompt = (string?)null, UseResume = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Missing prompt field in request body returns HTTP 400 Bad Request.
    /// </summary>
    [Fact]
    public async Task MissingPromptField_Returns400BadRequest()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/prompt", new { UseResume = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
