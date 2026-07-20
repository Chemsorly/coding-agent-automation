using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

/// <summary>
/// Serializes test classes that mutate the global Serilog Log.Logger to prevent
/// race conditions when xUnit runs tests in parallel.
/// </summary>
[CollectionDefinition("StaticLogger")]
public class StaticLoggerCollection;
