using System.Text.Json;
using System.Text.Json.Serialization;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Ui;

/// <summary>
/// Source-generated JSON metadata for the UI surface's DTOs. Each UI client adds the types it reads
/// and writes. The options match the API's: camelCase, enums as strings.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(UiMeResponse))]
[JsonSerializable(typeof(UiHealthResponse))]
[JsonSerializable(typeof(IReadOnlyList<UiProject>))]
[JsonSerializable(typeof(IReadOnlyList<UiTemplate>))]
[JsonSerializable(typeof(UiPipelineSettings))]
[JsonSerializable(typeof(UiFirstRunState))]
[JsonSerializable(typeof(UiMoveTemplateRequest))]
[JsonSerializable(typeof(PipelineConfiguration))]
[JsonSerializable(typeof(AgentProfile))]
[JsonSerializable(typeof(IReadOnlyList<AgentProfile>))]
[JsonSerializable(typeof(QualityGateConfiguration))]
[JsonSerializable(typeof(IReadOnlyList<QualityGateConfiguration>))]
[JsonSerializable(typeof(ReviewerConfiguration))]
[JsonSerializable(typeof(IReadOnlyList<ReviewerConfiguration>))]
[JsonSerializable(typeof(PipelineProject))]
[JsonSerializable(typeof(IReadOnlyList<PipelineProject>))]
[JsonSerializable(typeof(IReadOnlyList<PipelineJobTemplate>))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(ProviderConfig))]
[JsonSerializable(typeof(IReadOnlyList<ProviderConfig>))]
[JsonSerializable(typeof(UiProviderCheck))]
[JsonSerializable(typeof(IReadOnlyList<UiRepository>))]
[JsonSerializable(typeof(IReadOnlyList<AgentModelInfo>))]
[JsonSerializable(typeof(PagedResult<PipelineRunSummary>))]
[JsonSerializable(typeof(UiRunDetail))]
[JsonSerializable(typeof(IReadOnlyList<PendingWorkItemDto>))]
[JsonSerializable(typeof(IReadOnlyList<ActiveWorkItemDto>))]
[JsonSerializable(typeof(UiPriorityRequest))]
internal sealed partial class UiJsonContext : JsonSerializerContext;
