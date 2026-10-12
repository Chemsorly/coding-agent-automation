using CodingAgent.Pipeline.Models;

namespace CodingAgent.Contracts.Ui;

/// <summary>
/// The run page's data: the run, and the display name of the agent provider it ran with. The name
/// is resolved by the API, so the page needs no provider configs.
/// </summary>
public sealed record UiRunDetail(PipelineRunSummary Run, string? AgentProviderName);

/// <summary>Body of <c>PUT /api/ui/work-items/{id}/priority</c>: the dispatch weight, 0–1000.</summary>
public sealed record UiPriorityRequest(int PriorityWeight);
