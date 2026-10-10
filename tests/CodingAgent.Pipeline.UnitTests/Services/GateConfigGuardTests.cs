using AwesomeAssertions;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests;

public class GateConfigGuardTests
{
    [Theory]
    [InlineData("SonarQube.Analysis.xml")]
    [InlineData("sub/sonar-project.properties")]
    [InlineData(".editorconfig")]
    [InlineData("src/.editorconfig")]
    [InlineData("build/rules.ruleset")]
    [InlineData("tests/coverlet.runsettings")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".gitlab-ci.yml")]
    [InlineData("web/.eslintrc.json")]
    [InlineData("web/eslint.config.mjs")]
    [InlineData("codecov.yml")]
    [InlineData("SONARQUBE.ANALYSIS.XML")]
    [InlineData("src\\Analyzers.globalconfig")]
    public void FindGateConfigFiles_MatchesEachListEntry(string path)
    {
        GateConfigGuard.FindGateConfigFiles([path]).Should().Equal(path);
    }

    [Theory]
    [InlineData("src/Foo.cs")]
    [InlineData("README.md")]
    [InlineData("docs/sonar.md")]
    [InlineData("src/.github/workflows.cs")]
    [InlineData("Directory.Build.props")]
    public void FindGateConfigFiles_IgnoresOrdinaryFiles(string path)
    {
        GateConfigGuard.FindGateConfigFiles([path]).Should().BeEmpty();
    }

    [Fact]
    public void FindGateConfigFiles_SkipsEmptyLinesAndDuplicates()
    {
        var result = GateConfigGuard.FindGateConfigFiles(
            ["", "  ", ".editorconfig", "src/Foo.cs", " .editorconfig ", "SonarQube.Analysis.xml", ".editorconfig"]);

        result.Should().Equal(".editorconfig", "SonarQube.Analysis.xml");
    }

    [Fact]
    public async Task GetChangedFilesAsync_NoGitDirectory_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gate-config-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var result = await GateConfigGuard.GetChangedFilesAsync(dir, CancellationToken.None);

            result.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AppendWarningSection_ListsEachFileAsBullet()
    {
        var result = GateConfigGuard.AppendWarningSection("Original body", ["SonarQube.Analysis.xml", ".editorconfig"]);

        result.Should().StartWith("Original body");
        result.Should().Contain("## ⚠️ Quality-gate configuration changed");
        result.Should().Contain("- `SonarQube.Analysis.xml`");
        result.Should().Contain("- `.editorconfig`");
        result.Should().Contain("mark the PR ready for review");
    }
}
