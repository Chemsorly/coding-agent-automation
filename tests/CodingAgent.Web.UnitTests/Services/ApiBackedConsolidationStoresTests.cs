using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Moq;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Tests for the API-backed consolidation stores (<see cref="ApiBackedHarnessSuggestionStore"/>).
/// </summary>
public sealed class ApiBackedConsolidationStoresTests
{
    // ── ApiBackedHarnessSuggestionStore ─────────────────────────────

    [Fact]
    public async Task HarnessSuggestionStore_LoadAsync_DelegatesToClient()
    {
        // TODO: This test is tautological — the mock returns null and the assertion checks for null,
        // so a broken implementation (e.g. `return null;` bypassing the client) would still pass.
        // Fix by setting up the mock to return a non-null sentinel value and asserting the same
        // instance is returned, making a missing delegation call produce an actual failure.
        var mockClient = new Mock<CodingAgent.Api.Client.IPipelineApiHarnessSuggestionClient>();
        mockClient.Setup(c => c.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessSuggestions?)null);

        var store = new ApiBackedHarnessSuggestionStore(mockClient.Object);
        var result = await store.LoadAsync(CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task HarnessSuggestionStore_SaveAsync_DelegatesToClient()
    {
        var mockClient = new Mock<CodingAgent.Api.Client.IPipelineApiHarnessSuggestionClient>();
        mockClient.Setup(c => c.SaveAsync(It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var store = new ApiBackedHarnessSuggestionStore(mockClient.Object);
        var suggestions = new HarnessSuggestions
        {
            BasedOnRunCount = 1,
            GeneratedAtUtc = DateTime.UtcNow,
            SuccessRate = 0.5m,
            Suggestions = []
        };
        await store.SaveAsync(suggestions, CancellationToken.None);

        mockClient.Verify(c => c.SaveAsync(It.IsAny<HarnessSuggestions>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
