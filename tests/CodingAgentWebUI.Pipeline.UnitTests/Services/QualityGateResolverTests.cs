using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class QualityGateResolverTests
{
    private readonly QualityGateResolver _resolver = new();

    [Fact]
    public void Resolve_MatchingLabel_ReturnsQgc()
    {
        var qgcs = new List<QualityGateConfiguration>
        {
            new() { Id = "qgc-1", DisplayName = "DotNet", MatchLabels = ["dotnet"], CompilationCommand = "dotnet" }
        };

        var result = _resolver.Resolve(qgcs, ["kiro", "dotnet"]);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("qgc-1");
    }

    [Fact]
    public void Resolve_NoMatchingLabel_ReturnsEmpty()
    {
        var qgcs = new List<QualityGateConfiguration>
        {
            new() { Id = "qgc-1", DisplayName = "Python", MatchLabels = ["python"], CompilationCommand = "python" }
        };

        var result = _resolver.Resolve(qgcs, ["kiro", "dotnet"]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_EmptyMatchLabels_ActsAsGlobalFallback()
    {
        var qgcs = new List<QualityGateConfiguration>
        {
            new() { Id = "qgc-global", DisplayName = "Global", MatchLabels = [], CompilationCommand = "make" }
        };

        var result = _resolver.Resolve(qgcs, ["kiro", "dotnet"]);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("qgc-global");
    }

    [Fact]
    public void Resolve_DisabledQgc_IsExcluded()
    {
        var qgcs = new List<QualityGateConfiguration>
        {
            new() { Id = "qgc-1", DisplayName = "Disabled", MatchLabels = ["dotnet"], CompilationCommand = "dotnet", Enabled = false }
        };

        var result = _resolver.Resolve(qgcs, ["kiro", "dotnet"]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_MultipleMatches_OrderedByExecutionOrder()
    {
        var qgcs = new List<QualityGateConfiguration>
        {
            new() { Id = "qgc-security", DisplayName = "Security", MatchLabels = ["kiro"], CompilationCommand = "scan", ExecutionOrder = 10 },
            new() { Id = "qgc-compile", DisplayName = "Compile", MatchLabels = ["dotnet"], CompilationCommand = "dotnet", ExecutionOrder = 0 },
            new() { Id = "qgc-lint", DisplayName = "Lint", MatchLabels = ["kiro"], CompilationCommand = "lint", ExecutionOrder = 5 }
        };

        var result = _resolver.Resolve(qgcs, ["kiro", "dotnet"]);

        result.Should().HaveCount(3);
        result[0].Id.Should().Be("qgc-compile");
        result[1].Id.Should().Be("qgc-lint");
        result[2].Id.Should().Be("qgc-security");
    }

    [Fact]
    public void Resolve_SameExecutionOrder_OrderedByDisplayNameAlphabetically()
    {
        var qgcs = new List<QualityGateConfiguration>
        {
            new() { Id = "qgc-z", DisplayName = "Zebra", MatchLabels = ["dotnet"], CompilationCommand = "z", ExecutionOrder = 0 },
            new() { Id = "qgc-a", DisplayName = "Alpha", MatchLabels = ["dotnet"], CompilationCommand = "a", ExecutionOrder = 0 }
        };

        var result = _resolver.Resolve(qgcs, ["dotnet"]);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("qgc-a");
        result[1].Id.Should().Be("qgc-z");
    }

    [Fact]
    public void Resolve_CaseInsensitiveLabels_StillMatches()
    {
        var qgcs = new List<QualityGateConfiguration>
        {
            new() { Id = "qgc-1", DisplayName = "DotNet", MatchLabels = ["DotNet"], CompilationCommand = "dotnet" }
        };

        var result = _resolver.Resolve(qgcs, ["dotnet"]);

        result.Should().HaveCount(1);
    }

    [Fact]
    public void Resolve_EmptyJobLabels_OnlyGlobalFallbackMatches()
    {
        var qgcs = new List<QualityGateConfiguration>
        {
            new() { Id = "qgc-global", DisplayName = "Global", MatchLabels = [], CompilationCommand = "make" },
            new() { Id = "qgc-dotnet", DisplayName = "DotNet", MatchLabels = ["dotnet"], CompilationCommand = "dotnet" }
        };

        var result = _resolver.Resolve(qgcs, []);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("qgc-global");
    }

    [Fact]
    public void Resolve_EmptyConfigs_ReturnsEmpty()
    {
        var result = _resolver.Resolve([], ["kiro", "dotnet"]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_NullConfigs_ThrowsArgumentNullException()
    {
        var act = () => _resolver.Resolve(null!, ["kiro"]);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Resolve_NullJobLabels_ThrowsArgumentNullException()
    {
        var act = () => _resolver.Resolve([], null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Resolve_PartialLabelIntersection_Matches()
    {
        // QGC requires "dotnet" label, job has "kiro" and "dotnet" — intersection exists
        var qgcs = new List<QualityGateConfiguration>
        {
            new() { Id = "qgc-1", DisplayName = "DotNet", MatchLabels = ["dotnet", "linux"], CompilationCommand = "dotnet" }
        };

        var result = _resolver.Resolve(qgcs, ["kiro", "dotnet"]);

        result.Should().HaveCount(1);
    }
}
