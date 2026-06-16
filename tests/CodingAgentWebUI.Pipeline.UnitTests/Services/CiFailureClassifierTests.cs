using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class CiFailureClassifierTests
{
    [Theory]
    [InlineData("The self-hosted runner lost communication with the server")]
    [InlineData("Process completed with exit code 143")]
    [InlineData("The runner has received a shutdown signal")]
    [InlineData("Unable to write data to the transport connection")]
    [InlineData("Unable to resolve action `actions/checkout@v4`")]
    [InlineData("Could not resolve host: github.com")]
    [InlineData("Name or service not known")]
    [InlineData("Connection reset by peer")]
    [InlineData("Connection timed out after 30 seconds")]
    [InlineData("No space left on device")]
    [InlineData("Error downloading artifact 'build-output'")]
    [InlineData("failed to create shim task: OCI runtime create failed")]
    [InlineData("Error response from daemon: dial tcp 127.0.0.1:2376: connection refused")]
    [InlineData("Cache service responded with 503")]
    [InlineData("Package restore failed: Unable to load the service index for source")]
    public void Classify_InfrastructurePattern_ReturnsInfrastructure(string logContent)
    {
        var status = CreateStatus(logContent);
        CiFailureClassifier.Classify(status).Should().Be(CiFailureClassifier.CiFailureCategory.Infrastructure);
    }

    [Theory]
    [InlineData("error CS1002: ; expected")]
    [InlineData("Build FAILED.\n  0 Warning(s)\n  1 Error(s)")]
    [InlineData("Failed!  - Failed:     1, Passed:    42, Skipped:     0, Total:    43")]
    [InlineData("FAILED MyTest.ShouldWork\n  Error Message:\n   Assert.Equal() Failure")]
    public void Classify_CodeFailurePattern_ReturnsCodeFailure(string logContent)
    {
        var status = CreateStatus(logContent);
        CiFailureClassifier.Classify(status).Should().Be(CiFailureClassifier.CiFailureCategory.CodeFailure);
    }

    [Theory]
    [InlineData("API rate limit exceeded for installation")]
    [InlineData("You have exceeded a secondary rate limit")]
    [InlineData("HTTP 429 Too Many Requests")]
    public void Classify_RateLimitPattern_ReturnsRateLimited(string logContent)
    {
        var status = CreateStatus(logContent);
        CiFailureClassifier.Classify(status).Should().Be(CiFailureClassifier.CiFailureCategory.RateLimited);
    }

    [Fact]
    public void Classify_MixedInfrastructureAndCodeFailure_ReturnsCodeFailure()
    {
        // Code failure indicators take priority over infrastructure patterns
        var logContent = "Connection timed out\nerror CS1002: ; expected\nBuild FAILED.";
        var status = CreateStatus(logContent);
        CiFailureClassifier.Classify(status).Should().Be(CiFailureClassifier.CiFailureCategory.CodeFailure);
    }

    [Fact]
    public void Classify_NullLogContent_ReturnsInfrastructure()
    {
        // When logs are unavailable (BlobNotFound / null), treat as infrastructure
        // so we use the lightweight infra-retry path instead of wasting agent budget
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new[] { new PipelineJobResult { Name = "build", State = PipelineRunState.Failed, LogContent = null } }
        };
        CiFailureClassifier.Classify(status).Should().Be(CiFailureClassifier.CiFailureCategory.Infrastructure);
    }

    [Fact]
    public void Classify_EmptyLogContent_ReturnsInfrastructure()
    {
        // Empty log content is equivalent to unavailable — likely a storage race
        var status = CreateStatus("");
        CiFailureClassifier.Classify(status).Should().Be(CiFailureClassifier.CiFailureCategory.Infrastructure);
    }

    [Fact]
    public void Classify_NoFailedJobs_ReturnsUnknown()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new[] { new PipelineJobResult { Name = "build", State = PipelineRunState.Passed } }
        };
        CiFailureClassifier.Classify(status).Should().Be(CiFailureClassifier.CiFailureCategory.Unknown);
    }

    [Fact]
    public void Classify_UnrecognizedLogContent_ReturnsUnknown()
    {
        var status = CreateStatus("Some completely unrecognized failure output");
        CiFailureClassifier.Classify(status).Should().Be(CiFailureClassifier.CiFailureCategory.Unknown);
    }

    [Fact]
    public void Classify_NullStatus_ThrowsArgumentNullException()
    {
        var act = () => CiFailureClassifier.Classify(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Classify_MultipleJobs_OneInfrastructureOneCode_ReturnsCodeFailure()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new[]
            {
                new PipelineJobResult { Name = "build", State = PipelineRunState.Failed, LogContent = "Connection timed out" },
                new PipelineJobResult { Name = "test", State = PipelineRunState.Failed, LogContent = "error CS1002: ; expected" }
            }
        };
        CiFailureClassifier.Classify(status).Should().Be(CiFailureClassifier.CiFailureCategory.CodeFailure);
    }

    [Fact]
    public void Classify_MultipleJobs_AllInfrastructure_ReturnsInfrastructure()
    {
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new[]
            {
                new PipelineJobResult { Name = "build", State = PipelineRunState.Failed, LogContent = "Connection timed out" },
                new PipelineJobResult { Name = "test", State = PipelineRunState.Failed, LogContent = "lost communication with the server" }
            }
        };
        CiFailureClassifier.Classify(status).Should().Be(CiFailureClassifier.CiFailureCategory.Infrastructure);
    }

    [Fact]
    public void Classify_MultipleJobs_SomeWithoutLogs_NoCodeFailure_ReturnsInfrastructure()
    {
        // When some jobs have infra-pattern logs and others have no logs at all (BlobNotFound),
        // the overall classification should be Infrastructure
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new[]
            {
                new PipelineJobResult { Name = "build", State = PipelineRunState.Failed, LogContent = "Connection timed out" },
                new PipelineJobResult { Name = "docker", State = PipelineRunState.Failed, LogContent = null }
            }
        };
        CiFailureClassifier.Classify(status).Should().Be(CiFailureClassifier.CiFailureCategory.Infrastructure);
    }

    [Fact]
    public void Classify_MultipleJobs_AllWithoutLogs_ReturnsInfrastructure()
    {
        // All failed jobs have unavailable logs — classic BlobNotFound race
        var status = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new[]
            {
                new PipelineJobResult { Name = "build", State = PipelineRunState.Failed, LogContent = null },
                new PipelineJobResult { Name = "docker", State = PipelineRunState.Failed, LogContent = null }
            }
        };
        CiFailureClassifier.Classify(status).Should().Be(CiFailureClassifier.CiFailureCategory.Infrastructure);
    }

    [Fact]
    public void Classify_CrlfLineEndings_MatchesCodeFailurePattern()
    {
        var logContent = "FAILED MyTest.ShouldWork\r\n  Error Message:\r\n   Assert.Equal() Failure";
        var status = CreateStatus(logContent);
        CiFailureClassifier.Classify(status).Should().Be(CiFailureClassifier.CiFailureCategory.CodeFailure);
    }

    private static PipelineRunStatus CreateStatus(string logContent) => new()
    {
        State = PipelineRunState.Failed,
        Jobs = new[] { new PipelineJobResult { Name = "build", State = PipelineRunState.Failed, LogContent = logContent } }
    };
}
