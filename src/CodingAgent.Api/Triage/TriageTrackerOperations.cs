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
/// comment and <c>agent:wont-do</c> when it is dismissed. Values people or agents wrote are masked with the
/// project's secrets (<see cref="TriageSecrets"/>), then sanitized.
/// </summary>
public class TriageTrackerOperations
{
    private readonly IConfigurationStore _config;
    private readonly IAgentProfileStore _profiles;
    private readonly IProviderFactory _providers;
    private readonly ILabelService _labels;
    private readonly ILogger _logger;

    public TriageTrackerOperations(
        IConfigurationStore config, IAgentProfileStore profiles, IProviderFactory providers, ILabelService labels, ILogger logger)
    {
        _config = config;
        _profiles = profiles;
        _providers = providers;
        _labels = labels;
        _logger = logger;
    }

    // The comments never name the app user: usernames are often e-mail addresses, and the tracker may be public.

    /// <summary>Asks the loop for another attempt: the feedback goes on the issue, then <c>agent:triage</c>.</summary>
    public virtual async Task RequestRerunAsync(TriageRecord triage, TriageFeedback feedback, CancellationToken ct)
    {
        var secrets = await TriageSecrets.LoadAsync(_config, _profiles, triage.ProjectId, ct);
        string V(string value) => Clean(value, secrets);

        var sb = new StringBuilder();
        sb.AppendLine("**Feedback via Coding Agent** for the next triage attempt:");
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
        // Strict: if the label is not set, no attempt runs, and the caller must say so
        await _labels.SwapLabelAsync(triage.IssueProviderConfigId!, triage.IssueIdentifier!, AgentLabels.Triage, LabelTargetKind.Issue, ct);
    }

    /// <summary>Lists the created issues on the triaged issue and ends its triage with <c>agent:done</c>.</summary>
    public virtual async Task ReportCreatedAsync(TriageRecord triage, IReadOnlyList<TriageCreatedIssue> created, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine(TriageConstants.SummaryMarker);
        sb.AppendLine("**Issues created from this triage:**");
        sb.AppendLine();
        foreach (var issue in created)
            sb.AppendLine($"- {Clean(issue.Repository, [])}: {issue.Url ?? "#" + Clean(issue.Identifier, [])}");

        await PostCommentAsync(triage, sb.ToString(), critical: false, ct);
        await SwapAsync(triage, AgentLabels.Done, ct);
    }

    /// <summary>Notes on the triaged issue that the triage was dismissed, and sets <c>agent:wont-do</c>.</summary>
    public virtual async Task ReportDismissedAsync(TriageRecord triage, CancellationToken ct)
    {
        var secrets = await TriageSecrets.LoadAsync(_config, _profiles, triage.ProjectId, ct);
        var reason = string.IsNullOrWhiteSpace(triage.DismissReason) ? "" : $": {Clean(triage.DismissReason, secrets)}";
        await PostCommentAsync(triage, $"The triage was dismissed in Coding Agent{reason}", critical: false, ct);
        await SwapAsync(triage, AgentLabels.WontDo, ct);
    }

    private async Task PostCommentAsync(TriageRecord triage, string body, bool critical, CancellationToken ct)
    {
        try
        {
            var config = await _config.GetProviderConfigByIdAsync(triage.IssueProviderConfigId!, ProviderKind.Issue, ct)
                ?? throw new InvalidOperationException($"The tracker of triage {triage.Id} is not configured");

            await using var provider = _providers.CreateIssueProvider(config);
            await provider.PostCommentAsync(triage.IssueIdentifier!, body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !critical)
        {
            _logger.Warning(ex, "Could not comment on the issue of triage {TriageId} (non-fatal)", triage.Id);
        }
    }

    /// <summary>Masks the raw value, then sanitizes it: masking after sanitizing would miss changed secrets.</summary>
    private static string Clean(string value, IReadOnlyCollection<KeyValuePair<string, string>> secrets) =>
        TextSanitizer.SanitizeMarkdown(SecretMasker.Mask(value.Trim(), secrets));

    private Task SwapAsync(TriageRecord triage, string label, CancellationToken ct) =>
        _labels.TrySwapLabelAsync(
            triage.IssueProviderConfigId!, triage.IssueIdentifier!, label, LabelTargetKind.Issue, _logger,
            $"TriageTrackerOperations (triage {triage.Id})", ct);
}
