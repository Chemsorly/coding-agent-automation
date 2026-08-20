namespace CodingAgentWebUI;

/// <summary>
/// Entry-point marker for <c>WebApplicationFactory&lt;T&gt;</c>.
///
/// The factory only uses its type argument to locate the assembly holding the entry point, and
/// this assembly's generated entry point is the global <c>Program</c> — the same simple name
/// <c>CodingAgentWebUI.Api</c> uses. The E2E harness runs both hosts side by side and so
/// references both assemblies, which makes the bare name ambiguous; it targets this marker
/// instead. See <c>CodingAgentWebUI.Api.ApiHostMarker</c> for the API's equivalent.
/// </summary>
public sealed class WebUiHostMarker;
