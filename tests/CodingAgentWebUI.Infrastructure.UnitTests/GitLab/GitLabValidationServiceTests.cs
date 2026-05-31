using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitLab;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Unit tests for <see cref="GitLabValidationService"/>.
/// Uses WireMock to simulate GitLab API responses at the HTTP level.
/// NGitLab appends /api/v4 to the base URL, so stubs use that path prefix.
/// </summary>
public class GitLabValidationServiceTests : IAsyncDisposable
{
    private readonly WireMockServer _server;
    private readonly GitLabValidationService _sut = new();

    public GitLabValidationServiceTests()
    {
        _server = WireMockServer.Start();
    }

    public ValueTask DisposeAsync()
    {
        _server.Stop();
        _server.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private string BaseUrl => _server.Url!;

    private void StubProject(int projectId, int statusCode, object? body = null)
    {
        var response = Response.Create()
            .WithStatusCode(statusCode)
            .WithHeader("Content-Type", "application/json");

        if (body is not null)
            response.WithBody(System.Text.Json.JsonSerializer.Serialize(body));

        _server.Given(Request.Create().WithPath($"/api/v4/projects/{projectId}").UsingGet())
            .RespondWith(response);
    }

    #region Input validation (no HTTP calls)

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateAsync_NullOrEmptyApiUrl_ReturnsFailure(string? apiUrl)
    {
        var result = await _sut.ValidateAsync(apiUrl!, "token", "123", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("API URL is required");
    }

    [Theory]
    [InlineData("ftp://gitlab.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not-a-url")]
    public async Task ValidateAsync_InvalidUrlScheme_ReturnsFailure(string apiUrl)
    {
        var result = await _sut.ValidateAsync(apiUrl, "token", "123", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("https:// or http://");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateAsync_NullOrEmptyToken_ReturnsFailure(string? token)
    {
        var result = await _sut.ValidateAsync("https://gitlab.com", token!, "123", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Access token is required");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateAsync_NullOrEmptyProjectId_ReturnsFailure(string? projectId)
    {
        var result = await _sut.ValidateAsync("https://gitlab.com", "token", projectId!, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Project ID is required");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12.5")]
    [InlineData("not-a-number")]
    public async Task ValidateAsync_NonNumericProjectId_ReturnsFailure(string projectId)
    {
        var result = await _sut.ValidateAsync("https://gitlab.com", "token", projectId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Invalid project ID");
    }

    #endregion

    #region HTTP error paths

    [Fact]
    public async Task ValidateAsync_401Response_ReturnsInvalidTokenMessage()
    {
        StubProject(123, 401, new { message = "401 Unauthorized" });

        var result = await _sut.ValidateAsync(BaseUrl, "bad-token", "123", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Invalid access token");
    }

    [Fact]
    public async Task ValidateAsync_404Response_ReturnsProjectNotFoundMessage()
    {
        StubProject(999, 404, new { message = "404 Project Not Found" });

        var result = await _sut.ValidateAsync(BaseUrl, "token", "999", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task ValidateAsync_403Response_ReturnsAccessDeniedMessage()
    {
        StubProject(123, 403, new { message = "403 Forbidden" });

        var result = await _sut.ValidateAsync(BaseUrl, "token", "123", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Access denied");
    }

    [Fact]
    public async Task ValidateAsync_ConnectivityError_ReturnsFailureMessage()
    {
        // Point at a URL that will refuse connections
        var result = await _sut.ValidateAsync(
            "http://127.0.0.1:1", "token", "123", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region Success path

    [Fact]
    public async Task ValidateAsync_SuccessResponse_ReturnsProjectPathAndAccessLevel()
    {
        StubProject(123, 200, new
        {
            id = 123,
            path_with_namespace = "my-group/my-project",
            http_url_to_repo = "https://gitlab.com/my-group/my-project.git",
            permissions = new
            {
                project_access = new { access_level = 40 },
                group_access = (object?)null
            }
        });

        var result = await _sut.ValidateAsync(BaseUrl, "valid-token", "123", CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ProjectPath.Should().Be("my-group/my-project");
        result.AccessLevel.Should().Be("Maintainer");
        result.ErrorMessage.Should().BeNull();
    }

    [Theory]
    [InlineData(10, "Guest")]
    [InlineData(20, "Reporter")]
    [InlineData(30, "Developer")]
    [InlineData(40, "Maintainer")]
    [InlineData(50, "Owner")]
    public async Task ValidateAsync_MapsAccessLevelCorrectly(int accessLevel, string expected)
    {
        StubProject(123, 200, new
        {
            id = 123,
            path_with_namespace = "group/project",
            http_url_to_repo = "https://gitlab.com/group/project.git",
            permissions = new
            {
                project_access = new { access_level = accessLevel },
                group_access = (object?)null
            }
        });

        var result = await _sut.ValidateAsync(BaseUrl, "token", "123", CancellationToken.None);

        result.Success.Should().BeTrue();
        result.AccessLevel.Should().Be(expected);
    }

    [Fact]
    public async Task ValidateAsync_GroupAccessHigherThanProject_UsesGroupAccess()
    {
        StubProject(123, 200, new
        {
            id = 123,
            path_with_namespace = "group/project",
            http_url_to_repo = "https://gitlab.com/group/project.git",
            permissions = new
            {
                project_access = new { access_level = 20 },
                group_access = new { access_level = 40 }
            }
        });

        var result = await _sut.ValidateAsync(BaseUrl, "token", "123", CancellationToken.None);

        result.Success.Should().BeTrue();
        result.AccessLevel.Should().Be("Maintainer");
    }

    [Fact]
    public async Task ValidateAsync_OnlyGroupAccess_UsesGroupAccess()
    {
        StubProject(123, 200, new
        {
            id = 123,
            path_with_namespace = "group/project",
            http_url_to_repo = "https://gitlab.com/group/project.git",
            permissions = new
            {
                project_access = (object?)null,
                group_access = new { access_level = 30 }
            }
        });

        var result = await _sut.ValidateAsync(BaseUrl, "token", "123", CancellationToken.None);

        result.Success.Should().BeTrue();
        result.AccessLevel.Should().Be("Developer");
    }

    [Fact]
    public async Task ValidateAsync_NoPermissions_ReturnsUnknown()
    {
        StubProject(123, 200, new
        {
            id = 123,
            path_with_namespace = "group/project",
            http_url_to_repo = "https://gitlab.com/group/project.git",
            permissions = new
            {
                project_access = (object?)null,
                group_access = (object?)null
            }
        });

        var result = await _sut.ValidateAsync(BaseUrl, "token", "123", CancellationToken.None);

        result.Success.Should().BeTrue();
        result.AccessLevel.Should().Be("Unknown");
    }

    #endregion
}
