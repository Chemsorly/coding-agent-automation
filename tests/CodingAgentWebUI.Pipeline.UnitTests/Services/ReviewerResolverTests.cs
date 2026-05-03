using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class ReviewerResolverTests
{
    private readonly ReviewerResolver _resolver = new();

    [Fact]
    public void Resolve_MatchingLabel_ReturnsConfig()
    {
        var configs = new List<ReviewerConfiguration>
        {
            new() { Id = "rc-1", DisplayName = "DotNet Review", MatchLabels = ["dotnet"], Agents = [new ReviewAgent { Name = "Correctness", Prompt = "Check correctness" }] }
        };

        var result = _resolver.Resolve(configs, ["kiro", "dotnet"]);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("rc-1");
    }

    [Fact]
    public void Resolve_NoMatchingLabel_ReturnsEmpty()
    {
        var configs = new List<ReviewerConfiguration>
        {
            new() { Id = "rc-1", DisplayName = "Python Review", MatchLabels = ["python"], Agents = [new ReviewAgent { Name = "Agent", Prompt = "Review" }] }
        };

        var result = _resolver.Resolve(configs, ["kiro", "dotnet"]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_EmptyMatchLabels_ActsAsGlobalFallback()
    {
        var configs = new List<ReviewerConfiguration>
        {
            new() { Id = "rc-global", DisplayName = "Global Review", MatchLabels = [], Agents = [new ReviewAgent { Name = "General", Prompt = "General review" }] }
        };

        var result = _resolver.Resolve(configs, ["kiro", "dotnet"]);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("rc-global");
    }

    [Fact]
    public void Resolve_DisabledConfig_IsExcluded()
    {
        var configs = new List<ReviewerConfiguration>
        {
            new() { Id = "rc-1", DisplayName = "Disabled", MatchLabels = ["dotnet"], Agents = [new ReviewAgent { Name = "Agent", Prompt = "Review" }], Enabled = false }
        };

        var result = _resolver.Resolve(configs, ["dotnet"]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_MultipleMatches_OrderedByExecutionOrder()
    {
        var configs = new List<ReviewerConfiguration>
        {
            new() { Id = "rc-security", DisplayName = "Security", MatchLabels = ["kiro"], Agents = [new ReviewAgent { Name = "Security", Prompt = "Security review" }], ExecutionOrder = 10 },
            new() { Id = "rc-style", DisplayName = "Style", MatchLabels = ["kiro"], Agents = [new ReviewAgent { Name = "Style", Prompt = "Style review" }], ExecutionOrder = 0 }
        };

        var result = _resolver.Resolve(configs, ["kiro"]);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("rc-style");
        result[1].Id.Should().Be("rc-security");
    }

    [Fact]
    public void Resolve_SameExecutionOrder_OrderedByDisplayNameAlphabetically()
    {
        var configs = new List<ReviewerConfiguration>
        {
            new() { Id = "rc-z", DisplayName = "Zebra", MatchLabels = ["dotnet"], Agents = [new ReviewAgent { Name = "Z", Prompt = "Z" }], ExecutionOrder = 0 },
            new() { Id = "rc-a", DisplayName = "Alpha", MatchLabels = ["dotnet"], Agents = [new ReviewAgent { Name = "A", Prompt = "A" }], ExecutionOrder = 0 }
        };

        var result = _resolver.Resolve(configs, ["dotnet"]);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("rc-a");
        result[1].Id.Should().Be("rc-z");
    }

    [Fact]
    public void Resolve_CaseInsensitiveLabels_StillMatches()
    {
        var configs = new List<ReviewerConfiguration>
        {
            new() { Id = "rc-1", DisplayName = "Test", MatchLabels = ["DotNet"], Agents = [new ReviewAgent { Name = "Agent", Prompt = "Review" }] }
        };

        var result = _resolver.Resolve(configs, ["dotnet"]);

        result.Should().HaveCount(1);
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

    // --- FlattenAgents ---

    [Fact]
    public void FlattenAgents_MultipleConfigs_FlattensAllAgents()
    {
        var configs = new List<ReviewerConfiguration>
        {
            new() { DisplayName = "Config1", Agents = [new ReviewAgent { Name = "A1", Prompt = "P1" }, new ReviewAgent { Name = "A2", Prompt = "P2" }] },
            new() { DisplayName = "Config2", Agents = [new ReviewAgent { Name = "B1", Prompt = "P3" }] }
        };

        var result = ReviewerResolver.FlattenAgents(configs);

        result.Should().HaveCount(3);
        result[0].Name.Should().Be("A1");
        result[1].Name.Should().Be("A2");
        result[2].Name.Should().Be("B1");
    }

    [Fact]
    public void FlattenAgents_NullInput_ReturnsEmpty()
    {
        var result = ReviewerResolver.FlattenAgents(null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void FlattenAgents_EmptyList_ReturnsEmpty()
    {
        var result = ReviewerResolver.FlattenAgents([]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void FlattenAgents_PreservesOrder()
    {
        var configs = new List<ReviewerConfiguration>
        {
            new() { DisplayName = "Second", Agents = [new ReviewAgent { Name = "S1", Prompt = "SP1" }] },
            new() { DisplayName = "First", Agents = [new ReviewAgent { Name = "F1", Prompt = "FP1" }] }
        };

        var result = ReviewerResolver.FlattenAgents(configs);

        // Order follows input config order, not alphabetical
        result[0].Name.Should().Be("S1");
        result[1].Name.Should().Be("F1");
    }

    [Fact]
    public void FlattenAgents_MapsToReviewAgentConfig()
    {
        var configs = new List<ReviewerConfiguration>
        {
            new() { DisplayName = "Test", Agents = [new ReviewAgent { Name = "MyAgent", Prompt = "Do review" }] }
        };

        var result = ReviewerResolver.FlattenAgents(configs);

        result[0].Name.Should().Be("MyAgent");
        result[0].Prompt.Should().Be("Do review");
    }
}
