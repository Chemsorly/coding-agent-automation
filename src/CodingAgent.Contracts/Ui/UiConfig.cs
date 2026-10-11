namespace CodingAgent.Contracts.Ui;

/// <summary>
/// A project as every role's pages list it (<c>GET /api/ui/projects</c>). It carries no settings,
/// prompts, secrets or MCP servers; those stay behind Settings.
/// </summary>
public sealed record UiProject(string Id, string Name, bool Enabled);

/// <summary>A job template as every role's pages list it (<c>GET /api/ui/projects/{id}/templates</c>).</summary>
public sealed record UiTemplate(string Id, string Name, bool Enabled);

/// <summary>
/// The pipeline settings that pages outside Settings read (<c>GET /api/ui/pipeline-settings</c>).
/// Global values; project overrides are not applied.
/// </summary>
public sealed record UiPipelineSettings(int MaxRetries, int TriageRetentionDays);
