using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services.Parsers;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class JacocoParserTests
{
    [Fact]
    public void ParseCoverage_ValidXml_ReturnsCorrectPercentage()
    {
        var file = CreateJacocoFile("""
            <?xml version="1.0"?>
            <report>
              <package name="com.example">
                <class name="MyClass">
                  <counter type="LINE" missed="4" covered="6"/>
                </class>
              </package>
            </report>
            """);

        try
        {
            JacocoParser.ParseCoverage([file]).Should().Be(60.0);
        }
        finally { Cleanup(file); }
    }

    [Fact]
    public void ParseCoverage_EmptyArray_ReturnsZero()
    {
        JacocoParser.ParseCoverage([]).Should().Be(0.0);
    }

    [Fact]
    public void ParseCoverage_NonExistentFile_ReturnsZero()
    {
        JacocoParser.ParseCoverage(["/nonexistent/path.xml"]).Should().Be(0.0);
    }

    [Fact]
    public void ParseCoverage_MalformedXml_ReturnsZero()
    {
        var file = CreateTempFile("not valid xml <<<<");
        try
        {
            JacocoParser.ParseCoverage([file]).Should().Be(0.0);
        }
        finally { Cleanup(file); }
    }

    [Fact]
    public void ParseCoverage_CounterNotUnderClass_IsIgnored()
    {
        var file = CreateJacocoFile("""
            <?xml version="1.0"?>
            <report>
              <counter type="LINE" missed="10" covered="90"/>
              <package name="com.example">
                <counter type="LINE" missed="10" covered="90"/>
                <class name="MyClass">
                  <counter type="LINE" missed="5" covered="5"/>
                </class>
              </package>
            </report>
            """);

        try
        {
            // Only class-level counter counts: 5/(5+5) = 50%
            JacocoParser.ParseCoverage([file]).Should().Be(50.0);
        }
        finally { Cleanup(file); }
    }

    [Fact]
    public void ParseCoverage_ZeroMissedAndCovered_ReturnsZero()
    {
        var file = CreateJacocoFile("""
            <?xml version="1.0"?>
            <report>
              <package name="com.example">
                <class name="MyClass">
                  <counter type="LINE" missed="0" covered="0"/>
                </class>
              </package>
            </report>
            """);

        try
        {
            JacocoParser.ParseCoverage([file]).Should().Be(0.0);
        }
        finally { Cleanup(file); }
    }

    [Fact]
    public void ParseCoverage_OnlyBranchCounters_ReturnsZero()
    {
        var file = CreateJacocoFile("""
            <?xml version="1.0"?>
            <report>
              <package name="com.example">
                <class name="MyClass">
                  <counter type="BRANCH" missed="2" covered="8"/>
                </class>
              </package>
            </report>
            """);

        try
        {
            JacocoParser.ParseCoverage([file]).Should().Be(0.0);
        }
        finally { Cleanup(file); }
    }

    [Fact]
    public void ParseCoverage_MultipleFiles_SumsCounters()
    {
        var file1 = CreateJacocoFile("""
            <?xml version="1.0"?>
            <report>
              <package name="com.example">
                <class name="ClassA">
                  <counter type="LINE" missed="3" covered="7"/>
                </class>
              </package>
            </report>
            """);

        var file2 = CreateJacocoFile("""
            <?xml version="1.0"?>
            <report>
              <package name="com.example">
                <class name="ClassB">
                  <counter type="LINE" missed="7" covered="3"/>
                </class>
              </package>
            </report>
            """);

        try
        {
            // Total: missed=10, covered=10 → 50%
            JacocoParser.ParseCoverage([file1, file2]).Should().Be(50.0);
        }
        finally { Cleanup(file1); Cleanup(file2); }
    }

    [Fact]
    public void ParseCoverage_NullInput_ThrowsNullReferenceException()
    {
        var act = () => JacocoParser.ParseCoverage(null!);
        act.Should().Throw<NullReferenceException>();
    }

    [Property(MaxTest = 20)]
    public Property ParseCoverage_RandomCounters_ResultMatchesManualCalculation()
    {
        var gen =
            from missed in Gen.Choose(0, 100)
            from covered in Gen.Choose(0, 100)
            where missed + covered > 0
            select (missed, covered);

        return Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var xml = $"""
                <?xml version="1.0"?>
                <report>
                  <package name="com.example">
                    <class name="MyClass">
                      <counter type="LINE" missed="{t.missed}" covered="{t.covered}"/>
                    </class>
                  </package>
                </report>
                """;
            var file = CreateTempFile(xml);
            try
            {
                var result = JacocoParser.ParseCoverage([file]);
                var expected = (double)t.covered / (t.missed + t.covered) * 100.0;
                (Math.Abs(result - expected) < 0.0001).Should().BeTrue();
            }
            finally { Cleanup(file); }
        });
    }

    private static string CreateJacocoFile(string content) => CreateTempFile(content);

    private static string CreateTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jacoco-test-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, content);
        return path;
    }

    private static void Cleanup(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
