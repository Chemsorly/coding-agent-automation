using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Moq;

namespace CodingAgent.Web.UnitTests.TestUtilities;

/// <summary>
/// Spec 051 2.3b: the drawers browse through <see cref="IUiBrowseClient"/>. Page tests that arrange
/// their data on provider mocks keep doing so: this client answers from those mocks, the way the
/// API's TemplateBrowseService calls the providers (15 per page, the selected labels passed on).
/// Readiness and the active-issue badges are empty unless a test sets them up on the returned mock.
/// </summary>
internal static class ProviderBackedBrowseClient
{
    public static Mock<IUiBrowseClient> Create(Func<IIssueProvider?> issues, Func<IRepositoryProvider?>? repository = null)
    {
        var browse = new Mock<IUiBrowseClient>();
        browse.Setup(b => b.GetIssuesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns<string, int, IReadOnlyList<string>?, CancellationToken>((_, page, labels, ct) =>
                AsTheApi(() => Issues(issues).ListOpenIssuesAsync(page, 15, labels, ct), "Failed to load issues"));
        browse.Setup(b => b.GetLabelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, ct) => AsTheApi(() => Issues(issues).ListRepositoryLabelsAsync(ct), "Failed to load labels"));
        browse.Setup(b => b.GetEpicLabelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, ct) => AsTheApi(() => Issues(issues).ListRepositoryLabelsAsync(ct), "Failed to load labels"));
        browse.Setup(b => b.GetEpicsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns<string, int, IReadOnlyList<string>?, CancellationToken>((_, page, labels, ct) =>
                AsTheApi(() => Issues(issues).ListOpenIssuesAsync(page, 8, [AgentLabels.Epic, .. labels ?? []], ct), "Failed to load epics"));
        browse.Setup(b => b.GetPullRequestsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns<string, int, IReadOnlyList<string>?, CancellationToken>((_, page, labels, ct) =>
                AsTheApi(() => (repository?.Invoke() ?? throw new InvalidOperationException("Repository provider not found for this template."))
                    .ListOpenPullRequestsAsync(page, 15, labels, ct), "Failed to load pull requests"));
        browse.Setup(b => b.GetIssueReadinessAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, DependencyCheckResult>());
        browse.Setup(b => b.GetActiveIssuesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UiActiveIssue>());
        browse.Setup(b => b.GetBacklogAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BacklogResult([], IsTruncated: false));
        return browse;
    }

    /// <summary>A client with nothing to browse, for page tests that never open a drawer.</summary>
    public static Mock<IUiBrowseClient> Empty() => Create(() => null);

    /// <summary>Like the API: a provider failure comes back as a refusal whose message names what failed.</summary>
    private static async Task<T> AsTheApi<T>(Func<Task<T>> call, string failure)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(ex.Message.StartsWith("Issue provider", StringComparison.Ordinal) || ex.Message.StartsWith("Repository provider", StringComparison.Ordinal)
                ? ex.Message
                : $"{failure}: {ex.Message}", ex);
        }
    }

    private static IIssueProvider Issues(Func<IIssueProvider?> issues) =>
        issues() ?? throw new InvalidOperationException("Issue provider not found for this template.");
}
