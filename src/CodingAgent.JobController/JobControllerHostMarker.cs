namespace CodingAgent.JobController;

/// <summary>
/// Entry-point marker for <c>WebApplicationFactory&lt;T&gt;</c>.
///
/// The factory only uses its type argument to locate the assembly holding the entry point.
/// When the E2E harness hosts <c>CodingAgent.JobController</c> alongside <c>CodingAgent.Api</c>
/// and <c>CodingAgent.Web</c>, all three assemblies expose a top-level <c>Program</c> class
/// in the global namespace. Using those directly creates an ambiguous reference at the call site.
/// This marker resolves that by giving the factory a namespaced type to reference.
/// </summary>
public sealed class JobControllerHostMarker
{
    public JobControllerHostMarker() { }
}
