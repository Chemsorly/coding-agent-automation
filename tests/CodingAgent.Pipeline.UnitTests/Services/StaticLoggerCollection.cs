namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// xUnit collection that serialises all tests which mutate <c>Serilog.Log.Logger</c> (the global
/// static logger). Tests that temporarily replace the global logger with a capturing sink must
/// belong to this collection to prevent flaky empty-capture failures caused by parallel tests
/// overwriting each other's logger assignment.
/// </summary>
[CollectionDefinition("StaticLogger")]
public class StaticLoggerCollection;
