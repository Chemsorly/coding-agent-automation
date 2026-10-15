namespace CodingAgent.Contracts.Ui;

/// <summary>
/// What an operator triage of a project will use (<c>GET /api/ui/triages/preview</c>), shown on the new-triage
/// form so the operator can judge whether the agent can see what it needs.
/// </summary>
public sealed record TriagePreview
{
    /// <summary>The project's enabled repositories; the executor runs the job, the others are read-only.</summary>
    public IReadOnlyList<TriagePreviewRepository> Repositories { get; init; } = [];

    /// <summary>The MCP servers merged into the run (agent profile, then project), by name.</summary>
    public IReadOnlyList<TriagePreviewServer> McpServers { get; init; } = [];

    /// <summary>Trackers whose open issues the agent downloads.</summary>
    public int TrackerCount { get; init; }

    public int MaxOpenIssuesPerTracker { get; init; }
    public bool HasBrain { get; init; }
    public bool HasProjectSteering { get; init; }
    public TimeSpan TimeLimit { get; init; }

    /// <summary>Why the run cannot be dispatched as configured, or null.</summary>
    public string? Problem { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public TriagePreviewRepository? Executor => Repositories.FirstOrDefault(r => r.IsExecutor);
}

public sealed record TriagePreviewRepository(string TemplateId, string Name, bool IsExecutor);

/// <param name="Origin"><c>profile</c> or <c>project</c>.</param>
public sealed record TriagePreviewServer(string Name, string Origin);

