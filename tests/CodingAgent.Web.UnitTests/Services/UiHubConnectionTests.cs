using AwesomeAssertions;
using CodingAgent.Web.Ui;

namespace CodingAgent.Web.UnitTests.Services;

public class UiHubConnectionTests
{
    private sealed class NoToken : IUiAccessTokenSource
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
    }

    /// <summary>
    /// DisposeAsync stops the inner connection before disposing it; the other order throws
    /// ObjectDisposedException. Exercised without ever connecting.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WithoutEverConnecting_DoesNotThrow()
    {
        var connection = new UiHubConnection(new Uri("http://localhost:59999/hubs/ui"), new NoToken());

        await FluentActions.Awaiting(() => connection.DisposeAsync().AsTask()).Should().NotThrowAsync();
    }
}
