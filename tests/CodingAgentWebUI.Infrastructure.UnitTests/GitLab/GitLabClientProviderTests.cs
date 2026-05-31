using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.GitLab;
using Moq;
using NGitLab;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Unit tests for <see cref="GitLabClientProvider"/>.
/// Covers client caching, token refresh, and error paths.
/// </summary>
public class GitLabClientProviderTests : IAsyncDisposable
{
    #region Constructor validation

    [Fact]
    public void Constructor_NullApiUrl_Throws()
    {
        var act = () => new GitLabClientProvider(null!, "token");

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullToken_Throws()
    {
        var act = () => new GitLabClientProvider("https://gitlab.example.com", (string)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullTokenProvider_Throws()
    {
        var act = () => new GitLabClientProvider("https://gitlab.example.com", (Func<CancellationToken, Task<string>>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        var act = () => new GitLabClientProvider((IGitLabClient)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Static token constructor

    [Fact]
    public async Task GetClientAsync_StaticToken_ReturnsSameClient()
    {
        await using var provider = new GitLabClientProvider("https://gitlab.example.com", "static-token");

        var client1 = await provider.GetClientAsync(CancellationToken.None);
        var client2 = await provider.GetClientAsync(CancellationToken.None);

        client1.Should().BeSameAs(client2);
    }

    [Fact]
    public async Task GetTokenAsync_StaticToken_ReturnsToken()
    {
        await using var provider = new GitLabClientProvider("https://gitlab.example.com", "my-static-token");

        var token = await provider.GetTokenAsync(CancellationToken.None);

        token.Should().Be("my-static-token");
    }

    #endregion

    #region Test client constructor

    [Fact]
    public async Task GetClientAsync_WithTestClient_ReturnsSameInstance()
    {
        var mockClient = Mock.Of<IGitLabClient>();
        await using var provider = new GitLabClientProvider(mockClient);

        var result = await provider.GetClientAsync(CancellationToken.None);

        result.Should().BeSameAs(mockClient);
    }

    [Fact]
    public async Task GetTokenAsync_WithTestClient_ThrowsInvalidOperationException()
    {
        var mockClient = Mock.Of<IGitLabClient>();
        await using var provider = new GitLabClientProvider(mockClient);

        var act = () => provider.GetTokenAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region Dynamic token provider

    [Fact]
    public async Task GetClientAsync_DynamicToken_CachesClientWhenTokenUnchanged()
    {
        await using var provider = new GitLabClientProvider(
            "https://gitlab.example.com", _ => Task.FromResult("token-1"));

        var client1 = await provider.GetClientAsync(CancellationToken.None);
        var client2 = await provider.GetClientAsync(CancellationToken.None);

        client1.Should().BeSameAs(client2);
    }

    [Fact]
    public async Task GetClientAsync_DynamicToken_RecreatesClientWhenTokenChanges()
    {
        var token = "token-1";
        await using var provider = new GitLabClientProvider(
            "https://gitlab.example.com", _ => Task.FromResult(token));

        var client1 = await provider.GetClientAsync(CancellationToken.None);
        token = "token-2";
        var client2 = await provider.GetClientAsync(CancellationToken.None);

        client2.Should().NotBeSameAs(client1);
    }

    [Fact]
    public async Task GetTokenAsync_DynamicToken_ReturnsCurrentToken()
    {
        await using var provider = new GitLabClientProvider(
            "https://gitlab.example.com", _ => Task.FromResult("my-token"));

        var result = await provider.GetTokenAsync(CancellationToken.None);

        result.Should().Be("my-token");
    }

    [Fact]
    public async Task GetClientAsync_ConcurrentCalls_DoNotCorruptState()
    {
        var callCount = 0;
        await using var provider = new GitLabClientProvider(
            "https://gitlab.example.com", _ =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromResult("token-1");
            });

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => provider.GetClientAsync(CancellationToken.None))
            .ToArray();

        var clients = await Task.WhenAll(tasks);

        // All should return the same cached client (same token)
        clients.Should().AllSatisfy(c => c.Should().BeSameAs(clients[0]));
    }

    #endregion

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
