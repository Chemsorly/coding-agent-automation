using CodingAgent.Pipeline.Models;

namespace CodingAgent.Contracts.Ui;

/// <summary>The run page's data: the run, and the agent it ran with (spec 053 req 8.3).</summary>
public sealed record UiRunDetail(PipelineRunSummary Run, UiRunAgent Agent);

/// <summary>
/// The agent a run ran with: profile, model and effort as the run recorded them. A run recorded before spec 053 has
/// only its model; <paramref name="ProfileName"/> and <paramref name="Effort"/> are null.
/// </summary>
public sealed record UiRunAgent(string? ProfileName, string? Model, string? Effort);

/// <summary>Body of <c>PUT /api/ui/work-items/{id}/priority</c>: the dispatch weight, 0–1000.</summary>
public sealed record UiPriorityRequest(int PriorityWeight);
