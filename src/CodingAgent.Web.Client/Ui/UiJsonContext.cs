using System.Text.Json;
using System.Text.Json.Serialization;
using CodingAgent.Contracts.Auth;

namespace CodingAgent.Web.Ui;

/// <summary>Source-generated JSON metadata for the UI surface's DTOs. Each UI client adds the types it reads.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(UiMeResponse))]
internal sealed partial class UiJsonContext : JsonSerializerContext;
