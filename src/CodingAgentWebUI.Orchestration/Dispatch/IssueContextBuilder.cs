using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Encapsulates the shared issue context preparation logic used by both
/// <see cref="AgentJobDispatcher"/> and <see cref="DispatchOrchestrationService"/>.
/// Fetches issue details, parses the description, caps comments at 50,
/// and detects basic staleness signals (gate_rejection, gate_wont_do).
/// </summary>
/// <remarks>
/// This class does NOT invoke <see cref="AnalysisStalenessDetector"/> — that remains
/// in <see cref="DispatchOrchestrationService.PrepareCoreAsync"/> because it depends
/// on the pipeline configuration threshold which is resolved after issue context is built.
/// </remarks>
internal sealed class IssueContextBuilder
{
    private readonly IProviderFactory _providerFactory;
    private readonly IProviderConfigStore _providerConfigStore;

    public IssueContextBuilder(IProviderFactory providerFactory, IProviderConfigStore providerConfigStore)
    {
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(providerConfigStore);

        _providerFactory = providerFactory;
        _providerConfigStore = providerConfigStore;
    }

    /// <summary>
    /// Pre-fetches issue details, comments, and detects existing analysis with basic staleness signals.
    /// Returns <c>null</c> if the issue provider config is not found.
    /// </summary>
    /// <param name="issueIdentifier">The issue identifier (e.g., issue number or slug).</param>
    /// <param name="issueProviderId">The provider config ID for the issue provider.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The built <see cref="IssueContext"/>, or <c>null</c> if the provider config was not found.</returns>
    public async Task<IssueContext?> BuildAsync(
        string issueIdentifier,
        string issueProviderId,
        CancellationToken ct)
    {
        var issueConfig = await _providerConfigStore
            .GetProviderConfigByIdAsync(issueProviderId, ProviderKind.Issue, ct);
        if (issueConfig is null)
            return null;

        IssueDetail issueDetail;
        ParsedIssue parsedIssue;
        IReadOnlyList<IssueComment> issueComments;
        await using (var issueProvider = _providerFactory.CreateIssueProvider(issueConfig))
        {
            issueDetail = await issueProvider.GetIssueAsync(issueIdentifier, ct);
            parsedIssue = new IssueDescriptionParser().Parse(issueDetail.Description);
            var allComments = await issueProvider.ListCommentsAsync(issueIdentifier, ct);
            // Cap at 50 comments per REQ-4.4
            issueComments = allComments.Count > 50
                ? allComments.Take(50).ToList().AsReadOnly()
                : allComments;
        }

        // Detect existing analysis and rework state from comments.
        // NOTE: Only gate_rejection and gate_wont_do are detected here.
        // The three AnalysisStalenessDetector signals (body_changed, agent_error,
        // commit_threshold) are evaluated separately in PrepareCoreAsync because
        // they depend on pipeline configuration resolved after this step.
        string? existingAnalysis = null;
        bool forceRefreshAnalysis = false;
        string? stalenessSignal = null;
        var analysisComment = issueComments
            .Where(c => c.Body.Contains(CommentMarkers.AnalysisHeader))
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefault();
        if (analysisComment is not null)
        {
            existingAnalysis = analysisComment.Body;
            var gateRejection = issueComments
                .FirstOrDefault(c => c.Body.Contains(CommentMarkers.GateRejection));
            var gateWontDo = issueComments
                .FirstOrDefault(c => c.Body.Contains(CommentMarkers.GateWontDo));
            if (gateRejection?.CreatedAt > analysisComment.CreatedAt)
            {
                forceRefreshAnalysis = true;
                stalenessSignal = "gate_rejection";
            }
            else if (gateWontDo?.CreatedAt > analysisComment.CreatedAt)
            {
                forceRefreshAnalysis = true;
                stalenessSignal = "gate_wont_do";
            }
        }

        return new IssueContext(
            issueDetail, parsedIssue, issueComments,
            existingAnalysis, forceRefreshAnalysis, stalenessSignal, 0);
    }
}
