using System.Reflection;
using Mono.Cecil;
using NetArchTest.Rules;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Architecture;

/// <summary>
/// Enforces maximum constructor parameter count for service classes.
/// Prevents constructor over-injection (god object anti-pattern).
/// </summary>
public class ConstructorParameterTests
{
    private static readonly Assembly PipelineAssembly =
        typeof(PipelineOrchestrationService).Assembly;

    [Fact]
    public void PipelineOrchestrationService_ShouldHave_AtMost9NonLoggerParameters()
    {
        var ctors = typeof(PipelineOrchestrationService).GetConstructors();
        Assert.Single(ctors);

        var parameters = ctors[0].GetParameters();
        // Count non-logger parameters (ILogger is a cross-cutting concern, not counted per convention)
        var nonLoggerParams = parameters.Where(p => p.ParameterType != typeof(Serilog.ILogger)).ToArray();

        Assert.True(
            nonLoggerParams.Length <= 9,
            $"PipelineOrchestrationService has {nonLoggerParams.Length} non-logger constructor parameters (max 9). " +
            $"Parameters: {string.Join(", ", nonLoggerParams.Select(p => $"{p.ParameterType.Name} {p.Name}"))}");
    }

    [Fact]
    public void Pipeline_Services_ShouldNotExceed_MaxConstructorParameters()
    {
        const int maxParams = 12; // Allow some headroom for other services

        var result = Types.InAssembly(PipelineAssembly)
            .That().AreClasses()
            .And().HaveNameEndingWith("Service")
            .And().ArePublic()
            .Should().MeetCustomRule(new MaxConstructorParametersRule(maxParams))
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            $"The following service classes exceed {maxParams} constructor parameters: " +
            string.Join(", ", result.FailingTypes?.Select(t => t.Name) ?? []));
    }
}

/// <summary>
/// NetArchTest custom rule that checks constructor parameter count.
/// </summary>
internal sealed class MaxConstructorParametersRule : NetArchTest.Rules.ICustomRule
{
    private readonly int _maxParameters;

    public MaxConstructorParametersRule(int maxParameters) => _maxParameters = maxParameters;

    public bool MeetsRule(TypeDefinition type)
    {
        // Use System.Reflection to check constructor parameters
        var runtimeType = Type.GetType(type.FullName + ", " + type.Module.Assembly.Name.Name);
        if (runtimeType == null) return true; // Can't resolve — skip

        var ctors = runtimeType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (ctors.Length == 0) return true;

        // Check the constructor with the most parameters
        var maxParamCount = ctors.Max(c => c.GetParameters().Length);
        return maxParamCount <= _maxParameters;
    }
}
