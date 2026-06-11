using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class AcceptanceCriteriaParserTests : IDisposable
{
    private readonly string _workspacePath;
    private readonly Mock<Serilog.ILogger> _mockLogger;

    public AcceptanceCriteriaParserTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"test-ac-parser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_workspacePath, ".agent"));
        _mockLogger = new Mock<Serilog.ILogger>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    [Fact]
    public async Task ParseAsync_ValidJson_ReturnsReport()
    {
        var json = """
        {
            "criteria": [
                {
                    "criterion": "REST endpoint returns paginated results",
                    "status": "compliant",
                    "evidence": "Implemented in UsersController.cs"
                },
                {
                    "criterion": "Must support filtering by role",
                    "status": "non_compliant",
                    "reasoning": "Not implemented"
                }
            ],
            "summary": "1 of 2 criteria addressed."
        }
        """;
        await WriteJsonAsync(json);

        var result = await AcceptanceCriteriaParser.ParseAsync(_workspacePath, _mockLogger.Object, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Criteria.Should().HaveCount(2);
        result.Criteria[0].Status.Should().Be(CriterionStatus.Compliant);
        result.Criteria[0].Evidence.Should().Be("Implemented in UsersController.cs");
        result.Criteria[1].Status.Should().Be(CriterionStatus.NonCompliant);
        result.Criteria[1].Reasoning.Should().Be("Not implemented");
        result.Summary.Should().Be("1 of 2 criteria addressed.");
    }

    [Fact]
    public async Task ParseAsync_EmptyCriteria_ReturnsReportWithEmptyList()
    {
        var json = """{"criteria": [], "summary": "No acceptance criteria found."}""";
        await WriteJsonAsync(json);

        var result = await AcceptanceCriteriaParser.ParseAsync(_workspacePath, _mockLogger.Object, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Criteria.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_MissingFile_ReturnsNull()
    {
        var result = await AcceptanceCriteriaParser.ParseAsync(_workspacePath, _mockLogger.Object, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ParseAsync_InvalidJson_ReturnsNull()
    {
        await WriteJsonAsync("{ invalid json }}}");

        var result = await AcceptanceCriteriaParser.ParseAsync(_workspacePath, _mockLogger.Object, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ParseAsync_EmptyFile_ReturnsNull()
    {
        await WriteJsonAsync("");

        var result = await AcceptanceCriteriaParser.ParseAsync(_workspacePath, _mockLogger.Object, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ParseAsync_NotApplicableStatus_Parsed()
    {
        var json = """
        {
            "criteria": [{"criterion": "Test", "status": "not_applicable", "reasoning": "Out of scope"}],
            "summary": "0 of 1 criteria addressed."
        }
        """;
        await WriteJsonAsync(json);

        var result = await AcceptanceCriteriaParser.ParseAsync(_workspacePath, _mockLogger.Object, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Criteria[0].Status.Should().Be(CriterionStatus.NotApplicable);
    }

    private async Task WriteJsonAsync(string content)
    {
        var filePath = Path.Combine(_workspacePath, AgentWorkspacePaths.AcceptanceCriteriaFilePath);
        await File.WriteAllTextAsync(filePath, content);
    }
}
