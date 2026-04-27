using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure;

/// <summary>
/// Adapts <see cref="IIssueProvider"/> to the narrow <see cref="IAgentIssueOperations"/> interface.
/// Used on the orchestrator side to wrap the active issue provider so that
/// <c>AgentExecutionOrchestrator</c> and <c>QualityGateOrchestrator</c> can operate
/// without depending on the full <see cref="IIssueProvider"/>.
/// </summary>
public sealed class IssueProviderAdapter : IAgentIssueOperations
{
    private readonly IIssueProvider _issueProvider;
    private readonly Serilog.ILogger _logger;

    public IssueProviderAdapter(IIssueProvider issueProvider, Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(issueProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _issueProvider = issueProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PostCommentAsync(string issueIdentifier, string body, CancellationToken ct)
    {
        await _issueProvider.PostCommentAsync(issueIdentifier, body, ct);
    }

    /// <inheritdoc />
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
