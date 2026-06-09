using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services.Parsers;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class CoberturaParserTests
{
    [Fact]
    public void ParseCoverage_ValidXml_ReturnsCorrectPercentage()
    {
        var file = CreateCoberturaFile("""
            <?xml version="1.0"?>
            <coverage>
              <packages>
                <package>
                  <classes>
                    <class filename="File.cs">
                      <lines>
                        <line number="1" hits="1"/>
                        <line number="2" hits="0"/>
                        <line number="3" hits="1"/>
                        <line number="4" hits="1"/>
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);

        try
        {
            var result = CoberturaParser.ParseCoverage([file]);
            result.Should().Be(75.0);
        }
        finally { Cleanup(file); }
    }

    [Fact]
    public void ParseCoverage_EmptyArray_ReturnsZero()
    {
        CoberturaParser.ParseCoverage([]).Should().Be(0.0);
    }

    [Fact]
    public void ParseCoverage_NonExistentFile_ReturnsZero()
    {
        CoberturaParser.ParseCoverage(["/nonexistent/path.xml"]).Should().Be(0.0);
    }

    [Fact]
    public void ParseCoverage_MalformedXml_ReturnsZero()
    {
        var file = CreateTempFile("not valid xml <<<<");
        try
        {
            CoberturaParser.ParseCoverage([file]).Should().Be(0.0);
        }
        finally { Cleanup(file); }
    }

    [Fact]
    public void ParseCoverage_MissingFilenameAttribute_SkipsClass()
    {
        var file = CreateCoberturaFile("""
            <?xml version="1.0"?>
            <coverage>
              <packages><package><classes>
                <class>
                  <lines><line number="1" hits="1"/></lines>
                </class>
              </classes></package></packages>
            </coverage>
            """);

        try
        {
            CoberturaParser.ParseCoverage([file]).Should().Be(0.0);
        }
        finally { Cleanup(file); }
    }

    [Fact]
    public void ParseCoverage_MissingNumberAttribute_SkipsLine()
    {
        var file = CreateCoberturaFile("""
            <?xml version="1.0"?>
            <coverage>
              <packages><package><classes>
                <class filename="File.cs">
                  <lines>
                    <line hits="1"/>
                    <line number="2" hits="1"/>
                  </lines>
                </class>
              </classes></package></packages>
            </coverage>
            """);

        try
        {
            CoberturaParser.ParseCoverage([file]).Should().Be(100.0);
        }
        finally { Cleanup(file); }
    }

    [Fact]
    public void ParseCoverage_MissingHitsAttribute_DefaultsToZero()
    {
        var file = CreateCoberturaFile("""
            <?xml version="1.0"?>
            <coverage>
              <packages><package><classes>
                <class filename="File.cs">
                  <lines>
                    <line number="1"/>
                  </lines>
                </class>
              </classes></package></packages>
            </coverage>
            """);

        try
        {
            CoberturaParser.ParseCoverage([file]).Should().Be(0.0);
        }
        finally { Cleanup(file); }
    }

    [Fact]
    public void ParseCoverage_MultipleFiles_MergesWithMaxHits()
    {
        var file1 = CreateCoberturaFile("""
            <?xml version="1.0"?>
            <coverage>
              <packages><package><classes>
                <class filename="File.cs">
                  <lines>
                    <line number="1" hits="0"/>
                    <line number="2" hits="3"/>
                  </lines>
                </class>
              </classes></package></packages>
            </coverage>
            """);

        var file2 = CreateCoberturaFile("""
            <?xml version="1.0"?>
            <coverage>
              <packages><package><classes>
                <class filename="File.cs">
                  <lines>
                    <line number="1" hits="2"/>
                    <line number="2" hits="0"/>
                  </lines>
                </class>
              </classes></package></packages>
            </coverage>
            """);

        try
        {
            // Line 1: max(0,2)=2>0 → covered; Line 2: max(3,0)=3>0 → covered
            CoberturaParser.ParseCoverage([file1, file2]).Should().Be(100.0);
        }
        finally { Cleanup(file1); Cleanup(file2); }
    }

    [Fact]
    public void ParseCoverage_NullInput_ThrowsNullReferenceException()
    {
        var act = () => CoberturaParser.ParseCoverage(null!);
        act.Should().Throw<NullReferenceException>();
    }

    [Property(MaxTest = 20)]
    public Property ParseCoverage_RandomLines_ResultMatchesManualCalculation()
    {
        var gen =
            from count in Gen.Choose(1, 20)
            from hits in Gen.ArrayOf(Gen.Choose(0, 10), count)
            select hits;

        return Prop.ForAll(gen.ToArbitrary(), hits =>
        {
            var xml = BuildCoberturaXml("Test.cs", hits);
            var file = CreateTempFile(xml);
            try
            {
                var result = CoberturaParser.ParseCoverage([file]);
                var total = hits.Length;
                var covered = hits.Count(h => h > 0);
                var expected = total > 0 ? (double)covered / total * 100.0 : 0.0;
                (Math.Abs(result - expected) < 0.0001).Should().BeTrue();
            }
            finally { Cleanup(file); }
        });
    }

    private static string BuildCoberturaXml(string filename, int[] lineHits)
    {
        var lines = string.Join("\n",
            lineHits.Select((hits, i) => $"<line number=\"{i + 1}\" hits=\"{hits}\"/>"));
        return $"""
            <?xml version="1.0"?>
            <coverage>
              <packages><package><classes>
                <class filename="{filename}">
                  <lines>{lines}</lines>
                </class>
              </classes></package></packages>
            </coverage>
            """;
    }

    private static string CreateCoberturaFile(string content) => CreateTempFile(content);

    private static string CreateTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cobertura-test-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, content);
        return path;
    }

    private static void Cleanup(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
