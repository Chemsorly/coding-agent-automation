using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Adapts <see cref="IIssueProvider"/> to <see cref="IAgentIssueOperations"/> for use
/// by <see cref="AgentPhaseExecutor"/> and <see cref="QualityGateExecutor"/>.
/// Implements the label swap logic (remove all agent labels, add new label) inline.
/// </summary>
internal sealed class IssueProviderIssueOperations : IAgentIssueOperations
{
    private readonly IIssueProvider _issueProvider;
    private readonly Serilog.ILogger _logger;

    public IssueProviderIssueOperations(IIssueProvider issueProvider, Serilog.ILogger logger)
    {
        _issueProvider = issueProvider;
        _logger = logger;
    }

    public Task PostCommentAsync(string issueIdentifier, string body, CancellationToken ct)
        => _issueProvider.PostCommentAsync(issueIdentifier, body, ct);

    public async Task SwapLabelAsync(string issueIdentifier, string newLabel, CancellationToken ct)
    {
        try
        {
            foreach (var label in AgentLabels.All)
                await _issueProvider.RemoveLabelAsync(issueIdentifier, label, ct);
            await _issueProvider.AddLabelAsync(issueIdentifier, newLabel, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to swap agent label to {Label} on issue {Issue}", newLabel, issueIdentifier);
        }
    }
}
