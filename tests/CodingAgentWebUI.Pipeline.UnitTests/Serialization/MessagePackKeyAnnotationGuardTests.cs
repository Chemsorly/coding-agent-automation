using System.Reflection;
using AwesomeAssertions;
using MessagePack;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Serialization;

/// <summary>
/// Compile-time guard: all public settable properties on [MessagePackObject] DTOs
/// must have either [Key(N)] or [IgnoreMember]. A property without either annotation
/// indicates a developer forgot to assign a key index when adding a new field.
/// </summary>
public class MessagePackKeyAnnotationGuardTests
{
    /// <summary>
    /// All [MessagePackObject]-annotated types in the Pipeline assembly must have
    /// [Key(N)] or [IgnoreMember] on every public property with a setter/init.
    /// Fails if a property is missing both — prevents silent schema drift.
    /// </summary>
    [Fact]
    public void AllMessagePackObjectProperties_HaveKeyOrIgnoreMember()
    {
        // TODO: Also scan typeof(InlineCommentSettings).Assembly (CodeReview assembly) —
        // InlineCommentSettings is reachable via PipelineConfiguration.CodeReview.InlineComments
        // but lives in a separate assembly not covered by this guard.
        var assembly = typeof(JobAssignmentMessage).Assembly;

        var annotatedTypes = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<MessagePackObjectAttribute>() is not null)
            .ToList();

        annotatedTypes.Should().NotBeEmpty("expected [MessagePackObject] types in the Pipeline assembly");

        var violations = new List<string>();

        foreach (var type in annotatedTypes)
        {
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite || p.GetMethod?.ReturnType != null && p.SetMethod != null);

            foreach (var prop in properties)
            {
                // Writable property (has set/init)
                if (prop.SetMethod is null) continue;

                var hasKey = prop.GetCustomAttribute<KeyAttribute>() is not null;
                var hasIgnore = prop.GetCustomAttribute<IgnoreMemberAttribute>() is not null;

                if (!hasKey && !hasIgnore)
                {
                    violations.Add($"{type.Name}.{prop.Name}");
                }
            }
        }

        violations.Should().BeEmpty(
            "all public settable properties on [MessagePackObject] types must have [Key(N)] or [IgnoreMember]. " +
            $"Missing annotations: {string.Join(", ", violations)}");
    }

    /// <summary>
    /// No duplicate [Key(N)] indices within a single [MessagePackObject] type.
    /// </summary>
    [Fact]
    public void AllMessagePackObjectTypes_HaveUniqueKeyIndices()
    {
        var assembly = typeof(JobAssignmentMessage).Assembly;

        var annotatedTypes = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<MessagePackObjectAttribute>() is not null);

        var violations = new List<string>();

        foreach (var type in annotatedTypes)
        {
            var keys = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => new { p.Name, Key = p.GetCustomAttribute<KeyAttribute>() })
                .Where(x => x.Key is not null)
                .GroupBy(x => x.Key!.IntKey)
                .Where(g => g.Count() > 1);

            foreach (var dup in keys)
            {
                var props = string.Join(", ", dup.Select(x => x.Name));
                violations.Add($"{type.Name}: Key({dup.Key}) used by [{props}]");
            }
        }

        violations.Should().BeEmpty(
            $"duplicate [Key(N)] indices found: {string.Join("; ", violations)}");
    }
}
