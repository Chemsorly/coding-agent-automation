using System.Text.Json;
using System.Text.Json.Serialization;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;

namespace CodingAgent.Web.Ui;

/// <summary>Source-generated JSON metadata for the UI surface's DTOs. Each UI client adds the types it reads.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(UiMeResponse))]
[JsonSerializable(typeof(UiHealthResponse))]
[JsonSerializable(typeof(IReadOnlyList<UiProject>))]
[JsonSerializable(typeof(IReadOnlyList<UiTemplate>))]
[JsonSerializable(typeof(UiPipelineSettings))]
internal sealed partial class UiJsonContext : JsonSerializerContext;
