namespace CodingAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// One triage: a problem report, its attempts, its drafts and the issues created from them. Maps to the
/// "Triages" table. The whole record lives in <see cref="Data"/>; the other columns exist for filtering.
/// </summary>
public class TriageEntity
{
    public Guid Id { get; set; }

    /// <summary>The project the triage belongs to. Not a foreign key: a deleted project's triages stay until retention.</summary>
    public Guid ProjectId { get; set; }

    /// <summary><c>Operator</c> or <c>Issue</c>.</summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// The tracker part of the triage's WorkItem key: the issue's tracker, or the operator-triage sentinel.
    /// With <see cref="KeyIdentifier"/> it finds the triage's running WorkItem and makes a tracker issue
    /// map to one triage.
    /// </summary>
    public string KeyProviderConfigId { get; set; } = "";

    /// <summary>The identifier part of the triage's WorkItem key: the issue, or <c>triage:{Id}</c>.</summary>
    public string KeyIdentifier { get; set; } = "";

    /// <summary>The stored state (see TriageState), as its JSON name.</summary>
    public string State { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>
    /// True while an attempt has neither a result nor a recorded outcome. The retention sweep copies the
    /// outcome of such attempts from their WorkItems before those expire.
    /// </summary>
    public bool HasOpenAttempt { get; set; }

    /// <summary>JSONB string: the list facts (TriageListFacts), so a list never reads <see cref="Data"/>.</summary>
    public string Facts { get; set; } = "{}";

    /// <summary>JSONB string: the whole TriageRecord.</summary>
    public string Data { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
