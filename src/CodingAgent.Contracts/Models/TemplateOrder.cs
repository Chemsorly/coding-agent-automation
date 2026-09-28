namespace CodingAgent.Pipeline.Models;

/// <summary>
/// The order of the templates in a project: by name, ignoring case, with the exact name and the ID
/// as tie-breakers so the order is stable. It is the loop order, and it decides which template runs
/// the project's epics: the first enabled one with <see cref="PipelineJobTemplate.DecompositionEnabled"/>.
/// </summary>
public static class TemplateOrder
{
    public static IOrderedEnumerable<T> ByName<T>(IEnumerable<T> source, Func<T, string> name, Func<T, string> id) =>
        source
            .OrderBy(name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name, StringComparer.Ordinal)
            .ThenBy(id, StringComparer.Ordinal);
}
