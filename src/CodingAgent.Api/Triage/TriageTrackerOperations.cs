using System.Text;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Api.Triage;

/// <summary>
/// What a tracker triage's issue gets when people act on the triage in the app: the feedback comment and
/// <c>agent:triage</c> for a re-run, a summary comment and <c>agent:done</c> after issues were created, a short
/// comment and <c>agent:wont-do</c> when it is dismissed. Values people or agents wrote are sanitized and the
/// project's and repositories' secret values are masked.
/// </summary>
public class TriageTrackerOperations
{
    private readonly IConfigurationStore _config;
    private readonly IProviderFactory _providers;
    private readonly ILabelService _labels;
    private readonly ILogger _logger;

    public TriageTrackerOperations(IConfigurationStore config, IProviderFactory providers, ILabelService labels, ILogger logger)
    {
        _config = config;
        _providers = providers;
        _labels = labels;
        _logger = logger;
    }

    /// <summary>Asks the loop for another attempt: the feedback goes on the issue, then <c>agent:triage</c>.</summary>
    public virtual async Task RequestRerunAsync(TriageRecord triage, TriageFeedback feedback, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Feedback from {V(feedback.Author ?? "a person")} via Coding Agent** for the next triage attempt:");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(feedback.Text))
        {
            sb.AppendLine(V(feedback.Text));
            sb.AppendLine();
        }
        foreach (var answer in feedback.Answers)
        {
            sb.AppendLine($"- **Q:** {V(answer.Question)}");
            sb.AppendLine($"  **A:** {V(answer.Answer)}");
        }

        await PostCommentAsync(triage, sb.ToString(), critical: true, ct);
        await SwapAsync(triage, AgentLabels.Triage, ct);
    }

    /// <summary>Lists the created issues on the triaged issue and ends its triage with <c>agent:done</c>.</summary>
    public virtual async Task ReportCreatedAsync(TriageRecord triage, IReadOnlyList<TriageCreatedIssue> created, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine(TriageConstants.SummaryMarker);
        sb.AppendLine("**Issues created from this triage:**");
        sb.AppendLine();
        foreach (var issue in created)
            sb.AppendLine($"- {V(issue.Repository)}: {issue.Url ?? "#" + V(issue.Identifier)}");

        await PostCommentAsync(triage, sb.ToString(), critical: false, ct);
        await SwapAsync(triage, AgentLabels.Done, ct);
    }

    /// <summary>Notes on the triaged issue that the triage was dismissed, and sets <c>agent:wont-do</c>.</summary>
    public virtual async Task ReportDismissedAsync(TriageRecord triage, CancellationToken ct)
    {
        var reason = string.IsNullOrWhiteSpace(triage.DismissReason) ? "" : $": {V(triage.DismissReason)}";
        await PostCommentAsync(triage, $"The triage was dismissed in Coding Agent by {V(triage.DismissedBy ?? "a person")}{reason}", critical: false, ct);
        await SwapAsync(triage, AgentLabels.WontDo, ct);
    }

    private async Task PostCommentAsync(TriageRecord triage, string body, bool critical, CancellationToken ct)
    {
        try
        {
            var config = await _config.GetProviderConfigByIdAsync(triage.IssueProviderConfigId!, ProviderKind.Issue, ct)
                ?? throw new InvalidOperationException($"The tracker of triage {triage.Id} is not configured");
            var project = await _config.GetProjectByIdAsync(triage.ProjectId, ct);
            var masked = project?.Secrets is { Count: > 0 } secrets ? SecretMasker.Mask(body, secrets) : body;

            await using var provider = _providers.CreateIssueProvider(config);
            await provider.PostCommentAsync(triage.IssueIdentifier!, masked, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !critical)
        {
            _logger.Warning(ex, "Could not comment on the issue of triage {TriageId} (non-fatal)", triage.Id);
        }
    }

    private Task SwapAsync(TriageRecord triage, string label, CancellationToken ct) =>
        _labels.TrySwapLabelAsync(
            triage.IssueProviderConfigId!, triage.IssueIdentifier!, label, LabelTargetKind.Issue, _logger,
            $"TriageTrackerOperations (triage {triage.Id})", ct);

    private static string V(string value) => TextSanitizer.SanitizeMarkdown(value.Trim());
}
