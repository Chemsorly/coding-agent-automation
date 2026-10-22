using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Ui;

/// <summary>
/// #3577: what a project admin (admin on the project, not the global admin) may change. The rest of a project or
/// template is the operator's and comes from the stored copy, whatever the request carries, so a browser that
/// calls the API directly can change no more than the project page and the Behaviour card show.
/// </summary>
internal static class ProjectAdminEdits
{
    /// <summary>
    /// The project to store: the edit, with the operator's fields from <paramref name="stored"/>. A project admin
    /// edits the name, description, overrides, steering, secrets, MCP servers and project review.
    /// </summary>
    public static PipelineProject Apply(PipelineProject stored, PipelineProject edit) => edit with
    {
        Enabled = stored.Enabled,
        EpicIssueProviderId = stored.EpicIssueProviderId,
        BrainReadOnly = stored.BrainReadOnly,
        BrainConsolidationReviewEnabled = stored.BrainConsolidationReviewEnabled,
    };

    /// <summary>
    /// The template to store: <paramref name="stored"/> with the Behaviour card's fields from the edit. Every other
    /// field (connections, the Binding card, triage and any field added later) keeps its stored value.
    /// </summary>
    public static PipelineJobTemplate Apply(PipelineJobTemplate stored, PipelineJobTemplate edit) => stored with
    {
        Name = edit.Name,
        ImplementationEnabled = edit.ImplementationEnabled,
        ReviewEnabled = edit.ReviewEnabled,
        DecompositionEnabled = edit.DecompositionEnabled,
        HousekeepingEnabled = edit.HousekeepingEnabled,
        HousekeepingConcurrencyLimit = edit.HousekeepingConcurrencyLimit,
        HousekeepingBranchCleanupEnabled = edit.HousekeepingBranchCleanupEnabled,
        Secrets = edit.Secrets,
        SetupSteps = edit.SetupSteps,
        SteeringContent = edit.SteeringContent,
        BlacklistedPaths = edit.BlacklistedPaths,
    };
}
