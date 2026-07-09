using AwesomeAssertions;
using Moq;
using Octokit;
using Serilog;
using Serilog.Events;
using Serilog.Core;
using CodingAgentWebUI.Infrastructure.GitHub;
using PipelineRateLimitExceededException = CodingAgentWebUI.Pipeline.Models.RateLimitExceededException;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitHub;

/// <summary>
/// Verifies that logging is emitted before throw statements in GitHub provider classes.
/// Tests cover: ParseIssueIdentifier, ValidateAsync, and rate-limit wrapping paths.
/// </summary>
public class ThrowLoggingTests : IDisposable
{
    private readonly CollectingSink _sink;
    private readonly ILogger _previousLogger;

    public ThrowLoggingTests()
    {
        _previousLogger = Log.Logger;
        _sink = new CollectingSink();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        Log.Logger = _previousLogger;
    }

    #region ParseIssueIdentifier logging

    [Fact]
    public void ParseIssueIdentifier_InvalidIdentifier_LogsWarningBeforeThrowing()
    {
        var act = () => TestableGitHubProviderForLogging.ExposedParseIssueIdentifier("not-a-number");

        act.Should().Throw<ArgumentException>();

        _sink.Events.Should().Contain(e =>
            e.Level == LogEventLevel.Warning &&
            e.MessageTemplate.Text.Contains("Invalid issue identifier"));
    }

    #endregion

    #region GitHubIssueProvider.ValidateAsync logging

    [Fact]
    public async Task ValidateAsync_PrivateKeyDecodeFailure_LogsErrorBeforeThrowing()
    {
        var mockClient = new Mock<IGitHubClient>();
        var mockRepo = new Mock<IRepositoriesClient>();
        mockClient.Setup(c => c.Repository).Returns(mockRepo.Object);
        mockRepo.Setup(r => r.Get(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new GitHubAuthException(GitHubAuthErrorKind.PrivateKeyDecodeFailure, "decode failed"));

        var provider = new GitHubIssueProvider(
            new GitHubConnectionInfo("https://api.github.com", "owner", "repo"),
            mockClient.Object);

        var act = () => provider.ValidateAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        _sink.Events.Should().Contain(e =>
            e.Level == LogEventLevel.Error &&
            e.MessageTemplate.Text.Contains("private key"));
    }

    [Fact]
    public async Task ValidateAsync_TokenExchangeFailure_LogsErrorBeforeThrowing()
    {
        var mockClient = new Mock<IGitHubClient>();
        var mockRepo = new Mock<IRepositoriesClient>();
        mockClient.Setup(c => c.Repository).Returns(mockRepo.Object);
        mockRepo.Setup(r => r.Get(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new GitHubAuthException(GitHubAuthErrorKind.TokenExchangeFailure, "exchange failed"));

        var provider = new GitHubIssueProvider(
            new GitHubConnectionInfo("https://api.github.com", "owner", "repo"),
            mockClient.Object);

        var act = () => provider.ValidateAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        _sink.Events.Should().Contain(e =>
            e.Level == LogEventLevel.Error &&
            e.MessageTemplate.Text.Contains("Authentication failed"));
    }

    [Fact]
    public async Task ValidateAsync_AuthorizationException_LogsErrorBeforeThrowing()
    {
        var mockClient = new Mock<IGitHubClient>();
        var mockRepo = new Mock<IRepositoriesClient>();
        mockClient.Setup(c => c.Repository).Returns(mockRepo.Object);

        var response = CreateForbiddenResponse();
        mockRepo.Setup(r => r.Get(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new AuthorizationException(response.Object));

        var provider = new GitHubIssueProvider(
            new GitHubConnectionInfo("https://api.github.com", "owner", "repo"),
            mockClient.Object);

        var act = () => provider.ValidateAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        _sink.Events.Should().Contain(e =>
            e.Level == LogEventLevel.Error &&
            e.MessageTemplate.Text.Contains("installation token was rejected"));
    }

    [Fact]
    public async Task ValidateAsync_NotFoundException_LogsErrorBeforeThrowing()
    {
        var mockClient = new Mock<IGitHubClient>();
        var mockRepo = new Mock<IRepositoriesClient>();
        mockClient.Setup(c => c.Repository).Returns(mockRepo.Object);

        var response = CreateNotFoundResponse();
        mockRepo.Setup(r => r.Get(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new NotFoundException(response.Object));

        var provider = new GitHubIssueProvider(
            new GitHubConnectionInfo("https://api.github.com", "owner", "repo"),
            mockClient.Object);

        var act = () => provider.ValidateAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        _sink.Events.Should().Contain(e =>
            e.Level == LogEventLevel.Error &&
            e.MessageTemplate.Text.Contains("Repository not found"));
    }

    #endregion

    #region GitHubIssueProvider.UpdateCommentAsync invalid comment ID logging

    [Fact]
    public async Task UpdateCommentAsync_InvalidCommentId_LogsWarningBeforeThrowing()
    {
        var mockClient = new Mock<IGitHubClient>();
        var provider = new GitHubIssueProvider(
            new GitHubConnectionInfo("https://api.github.com", "owner", "repo"),
            mockClient.Object);

        var act = () => provider.UpdateCommentAsync("123", "not-a-number", "body", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();

        _sink.Events.Should().Contain(e =>
            e.Level == LogEventLevel.Warning &&
            e.MessageTemplate.Text.Contains("Invalid comment identifier"));
    }

    #endregion

    #region ExecuteWithRateLimitHandlingAsync logging

    [Fact]
    public async Task ExecuteWithRateLimitHandlingAsync_RateLimitExceeded_LogsWarningBeforeThrowing()
    {
        var mockClient = new Mock<IGitHubClient>();
        var provider = new TestableGitHubProviderForLogging(mockClient.Object);

        var response = CreateForbiddenResponse();
        var octokitException = new Octokit.RateLimitExceededException(response.Object);

        var act = () => provider.InvokeWithRateLimitHandlingAsync<string>(
            () => throw octokitException);

        await act.Should().ThrowAsync<PipelineRateLimitExceededException>();

        _sink.Events.Should().Contain(e =>
            e.Level == LogEventLevel.Warning &&
            e.MessageTemplate.Text.Contains("rate limit exceeded"));
    }

    [Fact]
    public async Task ExecuteWithRateLimitHandlingAsync_AbuseException_LogsWarningBeforeThrowing()
    {
        var mockClient = new Mock<IGitHubClient>();
        var provider = new TestableGitHubProviderForLogging(mockClient.Object);

        var response = CreateForbiddenResponse(retryAfterSeconds: 60);
        var abuseException = new AbuseException(response.Object);

        var act = () => provider.InvokeWithRateLimitHandlingAsync<string>(
            () => throw abuseException);

        await act.Should().ThrowAsync<PipelineRateLimitExceededException>();

        _sink.Events.Should().Contain(e =>
            e.Level == LogEventLevel.Warning &&
            e.MessageTemplate.Text.Contains("rate limit exceeded"));
    }

    #endregion

    #region Helpers

    private static Mock<IResponse> CreateForbiddenResponse(int? retryAfterSeconds = null)
    {
        var headers = new Dictionary<string, string>();
        if (retryAfterSeconds.HasValue)
            headers["Retry-After"] = retryAfterSeconds.Value.ToString();

        var resetTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var rateLimit = new RateLimit(5000, 0, resetTime.ToUnixTimeSeconds());
        var apiInfo = new ApiInfo(
            new Dictionary<string, Uri>(), new List<string>(), new List<string>(),
            string.Empty, rateLimit);

        var response = new Mock<IResponse>();
        response.Setup(r => r.StatusCode).Returns(System.Net.HttpStatusCode.Forbidden);
        response.Setup(r => r.Headers).Returns(headers);
        response.Setup(r => r.Body).Returns("");
        response.Setup(r => r.ContentType).Returns("application/json");
        response.Setup(r => r.ApiInfo).Returns(apiInfo);
        return response;
    }

    private static Mock<IResponse> CreateNotFoundResponse()
    {
        var resetTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var rateLimit = new RateLimit(5000, 4999, resetTime.ToUnixTimeSeconds());
        var apiInfo = new ApiInfo(
            new Dictionary<string, Uri>(), new List<string>(), new List<string>(),
            string.Empty, rateLimit);

        var response = new Mock<IResponse>();
        response.Setup(r => r.StatusCode).Returns(System.Net.HttpStatusCode.NotFound);
        response.Setup(r => r.Headers).Returns(new Dictionary<string, string>());
        response.Setup(r => r.Body).Returns("");
        response.Setup(r => r.ContentType).Returns("application/json");
        response.Setup(r => r.ApiInfo).Returns(apiInfo);
        return response;
    }

    /// <summary>
    /// Simple sink that collects log events for assertion.
    /// </summary>
    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();
        public IReadOnlyList<LogEvent> Events => _events;
        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }

    /// <summary>
    /// Testable subclass that exposes protected methods for logging verification.
    /// </summary>
    private sealed class TestableGitHubProviderForLogging : GitHubProviderBase
    {
        public TestableGitHubProviderForLogging(IGitHubClient client)
            : base(new GitHubConnectionInfo("https://api.github.com", "owner", "repo"), client)
        {
        }

        public static int ExposedParseIssueIdentifier(string identifier)
            => ParseIssueIdentifier(identifier);

        public Task<T> InvokeWithRateLimitHandlingAsync<T>(Func<Task<T>> apiCall)
            => ExecuteWithRateLimitHandlingAsync(apiCall);

        public Task InvokeWithRateLimitHandlingAsync(Func<Task> apiCall)
            => ExecuteWithRateLimitHandlingAsync(apiCall);
    }

    #endregion
}
