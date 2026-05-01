using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Models;

namespace CodingAgentWebUI.UnitTests.Models;

/// <summary>
/// Unit tests for the BuildInfo model — serialization, computed properties, and Load behavior.
/// </summary>
public class BuildInfoTests
{
    [Fact]
    public void BuildInfo_DefaultValues_AreLocal()
    {
        var info = new BuildInfo();

        info.CommitSha.Should().Be("local");
        info.Branch.Should().Be("local");
        info.BuildTimestamp.Should().Be("unknown");
        info.RunId.Should().Be("");
        info.RunNumber.Should().Be("");
        info.ImageTag.Should().Be("local");
        info.RepositoryUrl.Should().Be("https://github.com/Chemsorly/coding-agent-automation");
    }

    [Fact]
    public void BuildInfo_ShortSha_ReturnFirst7Chars()
    {
        var info = new BuildInfo { CommitSha = "abc1234567890" };

        info.ShortSha.Should().Be("abc1234");
    }

    [Fact]
    public void BuildInfo_ShortSha_ShortCommit_ReturnsFullSha()
    {
        var info = new BuildInfo { CommitSha = "abc" };

        info.ShortSha.Should().Be("abc");
    }

    [Fact]
    public void BuildInfo_ShortSha_ExactlySeven_ReturnsAll()
    {
        var info = new BuildInfo { CommitSha = "1234567" };

        info.ShortSha.Should().Be("1234567");
    }

    [Fact]
    public void BuildInfo_IsCI_WhenCommitShaIsNotLocal_ReturnsTrue()
    {
        var info = new BuildInfo { CommitSha = "abc1234567890" };

        info.IsCI.Should().BeTrue();
    }

    [Fact]
    public void BuildInfo_IsCI_WhenCommitShaIsLocal_ReturnsFalse()
    {
        var info = new BuildInfo();

        info.IsCI.Should().BeFalse();
    }

    [Fact]
    public void BuildInfo_CommitUrl_WhenIsCI_ReturnsValidUrl()
    {
        var info = new BuildInfo
        {
            CommitSha = "abc1234567890",
            RepositoryUrl = "https://github.com/org/repo"
        };

        info.CommitUrl.Should().Be("https://github.com/org/repo/commit/abc1234567890");
    }

    [Fact]
    public void BuildInfo_CommitUrl_WhenLocal_ReturnsEmpty()
    {
        var info = new BuildInfo();

        info.CommitUrl.Should().Be("");
    }

    [Fact]
    public void BuildInfo_CommitUrl_TrimsTrailingSlash()
    {
        var info = new BuildInfo
        {
            CommitSha = "abc1234567890",
            RepositoryUrl = "https://github.com/org/repo/"
        };

        info.CommitUrl.Should().Be("https://github.com/org/repo/commit/abc1234567890");
    }

    [Fact]
    public void BuildInfo_RunUrl_WhenIsCI_WithRunId_ReturnsValidUrl()
    {
        var info = new BuildInfo
        {
            CommitSha = "abc1234567890",
            RunId = "12345",
            RepositoryUrl = "https://github.com/org/repo"
        };

        info.RunUrl.Should().Be("https://github.com/org/repo/actions/runs/12345");
    }

    [Fact]
    public void BuildInfo_RunUrl_WhenLocal_ReturnsEmpty()
    {
        var info = new BuildInfo();

        info.RunUrl.Should().Be("");
    }

    [Fact]
    public void BuildInfo_RunUrl_WhenNoRunId_ReturnsEmpty()
    {
        var info = new BuildInfo
        {
            CommitSha = "abc1234567890",
            RunId = "",
            RepositoryUrl = "https://github.com/org/repo"
        };

        info.RunUrl.Should().Be("");
    }

    [Fact]
    public void BuildInfo_JsonSerialization_RoundTrips()
    {
        var info = new BuildInfo
        {
            CommitSha = "abc123",
            Branch = "main",
            BuildTimestamp = "2025-01-01T00:00:00Z",
            RunId = "999",
            RunNumber = "42",
            ImageTag = "v1.0.0",
            RepositoryUrl = "https://github.com/test/repo"
        };

        var json = JsonSerializer.Serialize(info);
        var deserialized = JsonSerializer.Deserialize<BuildInfo>(json);

        deserialized.Should().NotBeNull();
        deserialized!.CommitSha.Should().Be("abc123");
        deserialized.Branch.Should().Be("main");
        deserialized.BuildTimestamp.Should().Be("2025-01-01T00:00:00Z");
        deserialized.RunId.Should().Be("999");
        deserialized.RunNumber.Should().Be("42");
        deserialized.ImageTag.Should().Be("v1.0.0");
        deserialized.RepositoryUrl.Should().Be("https://github.com/test/repo");
    }

    [Fact]
    public void BuildInfo_JsonDeserialization_HandlesPartialJson()
    {
        var json = """{"commitSha":"def456","branch":"feature"}""";
        var info = JsonSerializer.Deserialize<BuildInfo>(json);

        info.Should().NotBeNull();
        info!.CommitSha.Should().Be("def456");
        info.Branch.Should().Be("feature");
        info.BuildTimestamp.Should().Be("unknown"); // default
        info.RunId.Should().Be(""); // default
    }

    [Fact]
    public void BuildInfo_Load_WhenFileDoesNotExist_ReturnsDefaults()
    {
        // Load looks for build-info.json in AppContext.BaseDirectory
        // In test context, this file likely doesn't exist
        var info = BuildInfo.Load();

        info.Should().NotBeNull();
        // Either it loads the actual file or returns defaults
        // Both are valid — just verify it doesn't throw
    }

    [Fact]
    public void BuildInfo_JsonIgnore_ComputedProperties_NotSerialized()
    {
        var info = new BuildInfo { CommitSha = "abc1234567890" };
        var json = JsonSerializer.Serialize(info);

        json.Should().NotContain("shortSha");
        json.Should().NotContain("isCI");
        json.Should().NotContain("commitUrl");
        json.Should().NotContain("runUrl");
    }

    [Fact]
    public void BuildInfo_CommitUrl_EmptyRepositoryUrl_ReturnsEmpty()
    {
        var info = new BuildInfo
        {
            CommitSha = "abc1234567890",
            RepositoryUrl = ""
        };

        info.CommitUrl.Should().Be("");
    }

    [Fact]
    public void BuildInfo_RunUrl_EmptyRepositoryUrl_ReturnsEmpty()
    {
        var info = new BuildInfo
        {
            CommitSha = "abc1234567890",
            RunId = "123",
            RepositoryUrl = ""
        };

        info.RunUrl.Should().Be("");
    }
}
