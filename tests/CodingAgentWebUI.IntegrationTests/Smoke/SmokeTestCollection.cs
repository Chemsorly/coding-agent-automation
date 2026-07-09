namespace CodingAgentWebUI.IntegrationTests.Smoke;

/// <summary>
/// Serializes all smoke tests that use WebApplicationFactory to prevent parallel
/// execution. Multiple factories racing on Serilog's global Log.Logger causes
/// "The logger is already frozen" when UseSerilog's Freeze() is called concurrently.
/// </summary>
[CollectionDefinition("SmokeTests")]
public class SmokeTestCollection;
