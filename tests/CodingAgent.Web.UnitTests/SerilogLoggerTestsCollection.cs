namespace CodingAgent.Web.UnitTests;

/// <summary>
/// Serializes all tests that swap Serilog's global <see cref="Serilog.Log.Logger"/> so that
/// concurrent replacements do not contaminate each other's captured log events.
/// <c>DisableParallelization = true</c> additionally prevents this collection from running
/// alongside any other collection in this assembly, closing the window where a parallel
/// test's <c>Log.Logger</c> call would appear in another class's log-capture sink.
/// </summary>
[CollectionDefinition("SerilogLoggerTests", DisableParallelization = true)]
public class SerilogLoggerTestsCollection { }
