using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using Moq;
using Serilog;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// End-to-end integration tests verifying the full label mapping procedure:
/// QGC resolution and required-label resolution work correctly together.
/// </summary>
public class LabelMappingIntegrationTests
{
    private readonly QualityGateResolver _qgcResolver = new();

    [Fact]
    public void BasicDotNetRepo_QgcWithMatchingLabel_IsIncluded()
    {
        // Arrange: Job required labels ["kiro", "dotnet", "dotnet10"]
        var jobLabels = new List<string> { "kiro", "dotnet", "dotnet10" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-dotnet", "DotNet QGC", ["dotnet"]),
            CreateQgc("qgc-python", "Python QGC", ["python"])
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert: dotnet QGC included (intersection: "dotnet"), python QGC excluded
        resolved.Should().HaveCount(1);
        resolved[0].Id.Should().Be("qgc-dotnet");
    }

    [Fact]
    public void BasicDotNetRepo_QgcWithNonMatchingLabel_IsExcluded()
    {
        // Arrange
        var jobLabels = new List<string> { "kiro", "dotnet", "dotnet10" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-python", "Python QGC", ["python"])
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert
        resolved.Should().BeEmpty();
    }


    [Fact]
    public void PolyglotRepo_BothStackQgcs_AreIncluded()
    {
        // Arrange: Job labels ["kiro", "dotnet", "python"]
        var jobLabels = new List<string> { "kiro", "dotnet", "python" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-dotnet", "DotNet QGC", ["dotnet"]),
            CreateQgc("qgc-python", "Python QGC", ["python"]),
            CreateQgc("qgc-java", "Java QGC", ["java"])
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert: dotnet and python included, java excluded
        resolved.Should().HaveCount(2);
        resolved.Select(q => q.Id).Should().Contain("qgc-dotnet");
        resolved.Select(q => q.Id).Should().Contain("qgc-python");
        resolved.Select(q => q.Id).Should().NotContain("qgc-java");
    }


    #region Scenario 3: Global fallback QGC

    [Fact]
    public void GlobalFallbackQgc_AlwaysIncluded_RegardlessOfJobLabels()
    {
        // Arrange: QGC with empty matchLabels is a global fallback
        var jobLabels = new List<string> { "kiro", "dotnet", "dotnet10" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-global", "Global Fallback", []),
            CreateQgc("qgc-dotnet", "DotNet QGC", ["dotnet"])
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert: Both global and stack-specific are included
        resolved.Should().HaveCount(2);
        resolved.Select(q => q.Id).Should().Contain("qgc-global");
        resolved.Select(q => q.Id).Should().Contain("qgc-dotnet");
    }

    [Fact]
    public void GlobalFallbackQgc_IncludedAlongsideStackSpecific_NotInsteadOf()
    {
        // Arrange: Global + dotnet + python QGCs, job has dotnet labels
        var jobLabels = new List<string> { "kiro", "dotnet" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-global", "Global Fallback", []),
            CreateQgc("qgc-dotnet", "DotNet QGC", ["dotnet"]),
            CreateQgc("qgc-python", "Python QGC", ["python"])
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert: Global AND dotnet included, python excluded
        resolved.Should().HaveCount(2);
        resolved.Select(q => q.Id).Should().Contain("qgc-global");
        resolved.Select(q => q.Id).Should().Contain("qgc-dotnet");
    }

    #endregion

    #region Scenario 7: RequiredLabels from ProviderConfig.RequiredLabels field

    [Fact]
    public void RequiredLabels_ExplicitField_UsedForResolution()
    {
        // Arrange
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            RequiredLabels = new List<string> { "kiro", "dotnet", "dotnet10" }
        };
        var pipelineConfig = new PipelineConfiguration();

        // Act
        var labels = LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);

        // Assert
        labels.Should().HaveCount(3);
        labels.Should().Contain("kiro");
        labels.Should().Contain("dotnet");
        labels.Should().Contain("dotnet10");
    }

    #endregion

    #region Scenario 8: RequiredLabels fallback to pipeline default

    [Fact]
    public void RequiredLabels_FallsBackToPipelineDefault_WhenBothNull()
    {
        // Arrange: No RequiredLabels, but pipeline has default
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo",
            RequiredLabels = null
        };
        var pipelineConfig = new PipelineConfiguration
        {
            DefaultRequiredAgentLabels = "kiro,dotnet"
        };

        // Act
        var resolved = LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);

        // Assert: Falls back to pipeline default
        resolved.Should().BeEquivalentTo(new[] { "kiro", "dotnet" });
    }

    [Fact]
    public void RequiredLabels_ReturnsEmpty_WhenNothingConfigured()
    {
        // Arrange: No labels configured anywhere
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo"
        };
        var pipelineConfig = new PipelineConfiguration();

        // Act
        var resolved = LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);

        // Assert: Empty — any agent matches
        resolved.Should().BeEmpty();
    }

    #endregion

    #region Scenario 9: QGC execution order

    [Fact]
    public void QgcExecutionOrder_ResolvedInCorrectOrder()
    {
        // Arrange: Multiple QGCs with different execution orders
        var jobLabels = new List<string> { "kiro", "dotnet" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-security", "Security", ["kiro"], executionOrder: 10),
            CreateQgc("qgc-dotnet", "DotNet", ["dotnet"], executionOrder: 0),
            CreateQgc("qgc-global", "Global Fallback", [], executionOrder: 5)
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert: Ordered by ExecutionOrder: dotnet (0), global (5), security (10)
        resolved.Should().HaveCount(3);
        resolved[0].Id.Should().Be("qgc-dotnet");
        resolved[0].ExecutionOrder.Should().Be(0);
        resolved[1].Id.Should().Be("qgc-global");
        resolved[1].ExecutionOrder.Should().Be(5);
        resolved[2].Id.Should().Be("qgc-security");
        resolved[2].ExecutionOrder.Should().Be(10);
    }

    [Fact]
    public void QgcExecutionOrder_SameOrder_SortedByDisplayNameAlphabetically()
    {
        // Arrange: Two QGCs with same execution order
        var jobLabels = new List<string> { "kiro", "dotnet" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-z", "Zebra QGC", ["dotnet"], executionOrder: 0),
            CreateQgc("qgc-a", "Alpha QGC", ["dotnet"], executionOrder: 0)
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert: Alphabetical by DisplayName when ExecutionOrder is tied
        resolved.Should().HaveCount(2);
        resolved[0].DisplayName.Should().Be("Alpha QGC");
        resolved[1].DisplayName.Should().Be("Zebra QGC");
    }

    #endregion

    [Fact]
    public void CaseInsensitivity_QgcResolution_MatchesRegardlessOfCase()
    {
        // Arrange: QGC matchLabels lowercase, job labels mixed case
        var jobLabels = new List<string> { "Kiro", "DotNet" };
        var qgcs = new List<QualityGateConfiguration>
        {
            CreateQgc("qgc-dotnet", "DotNet QGC", ["dotnet"])
        };

        // Act
        var resolved = _qgcResolver.Resolve(qgcs, jobLabels);

        // Assert: Case-insensitive intersection
        resolved.Should().HaveCount(1);
        resolved[0].Id.Should().Be("qgc-dotnet");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static QualityGateConfiguration CreateQgc(
        string id, string displayName, string[] matchLabels, int executionOrder = 0) => new()
    {
        Id = id,
        DisplayName = displayName,
        MatchLabels = matchLabels.ToList(),
        TestArguments = ["test"],
        Enabled = true,
        ExecutionOrder = executionOrder
    };
}
