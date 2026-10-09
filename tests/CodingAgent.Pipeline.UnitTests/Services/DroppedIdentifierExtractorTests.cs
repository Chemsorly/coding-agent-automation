using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="DroppedIdentifierExtractor"/>.
/// </summary>
public class DroppedIdentifierExtractorTests
{
    // ── ExtractAddedIdentifiers ──────────────────────────────────────────────

    [Fact]
    public void ExtractAddedIdentifiers_ClassDeclaration_ReturnsClassName()
    {
        var diff = "@@ -0,0 +1,3 @@\n+public class MyService\n+{\n+}\n";

        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(diff);

        result.Should().Contain("MyService");
    }

    [Fact]
    public void ExtractAddedIdentifiers_RecordDeclaration_ReturnsRecordName()
    {
        var diff = "@@ -1,1 +1,1 @@\n+public record MyRecord(string Name);\n";

        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(diff);

        result.Should().Contain("MyRecord");
    }

    [Fact]
    public void ExtractAddedIdentifiers_InterfaceDeclaration_ReturnsInterfaceName()
    {
        var diff = "+public interface IMyInterface\n+{\n+}\n";

        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(diff);

        result.Should().Contain("IMyInterface");
    }

    [Fact]
    public void ExtractAddedIdentifiers_EnumDeclaration_ReturnsEnumName()
    {
        var diff = "+internal enum StatusCode { Active, Inactive }\n";

        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(diff);

        result.Should().Contain("StatusCode");
    }

    [Fact]
    public void ExtractAddedIdentifiers_TestMethod_FactAttribute_ReturnsMethodName()
    {
        var diff = "@@ -10,0 +11,5 @@\n" +
                   "+    [Fact]\n" +
                   "+    public void WhenFoo_ReturnsBar()\n" +
                   "+    {\n" +
                   "+        // assert\n" +
                   "+    }\n";

        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(diff);

        result.Should().Contain("WhenFoo_ReturnsBar");
    }

    [Fact]
    public void ExtractAddedIdentifiers_TestMethod_TheoryAttribute_ReturnsMethodName()
    {
        var diff = "+    [Theory]\n" +
                   "+    [InlineData(1)]\n" +
                   "+    public void Given_WhenX_ThenY(int n)\n" +
                   "+    {\n" +
                   "+    }\n";

        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(diff);

        result.Should().Contain("Given_WhenX_ThenY");
    }

    [Fact]
    public void ExtractAddedIdentifiers_EmptyDiff_ReturnsEmpty()
    {
        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers("");

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractAddedIdentifiers_NullDiff_ReturnsEmpty()
    {
        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(null!);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractAddedIdentifiers_DeletionLines_AreIgnored()
    {
        // Lines starting with - should not produce identifiers
        var diff = "-public class RemovedService\n-{\n-}\n";

        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(diff);

        result.Should().NotContain("RemovedService");
    }

    [Fact]
    public void ExtractAddedIdentifiers_MetadataLines_AreIgnored()
    {
        // +++ and --- header lines must not match
        var diff = "--- a/src/Foo.cs\n+++ b/src/Foo.cs\n@@ -0,0 +1 @@\n+public class Real\n";

        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(diff);

        result.Should().Contain("Real");
        result.Should().NotContain("b");   // +++ b/src/Foo.cs must not produce "b"
        result.Should().NotContain("src"); // same
    }

    [Fact]
    public void ExtractAddedIdentifiers_MultipleTypes_AllReturned()
    {
        var diff =
            "+public class TypeA\n" +
            "+{\n" +
            "+}\n" +
            "+internal interface ITypeB\n" +
            "+{\n" +
            "+}\n" +
            "+public enum TypeC { }\n";

        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(diff);

        result.Should().Contain("TypeA");
        result.Should().Contain("ITypeB");
        result.Should().Contain("TypeC");
    }

    [Fact]
    public void ExtractAddedIdentifiers_ContextLines_AreIgnored()
    {
        // Lines with no leading + or - are context lines
        var diff = " public class ContextOnly\n{\n}\n";

        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(diff);

        result.Should().NotContain("ContextOnly");
    }

    [Fact]
    public void ExtractAddedIdentifiers_TestClassWithMethods_ReturnsAll()
    {
        // Simulates the SynchronousDispatchEndpointTests.cs scenario from the issue
        var diff =
            "+public class WorkItemDispatchEndpointHelperCoverageTests\n" +
            "+{\n" +
            "+    [Fact]\n" +
            "+    public void WhenNoHelper_ReturnsNull()\n" +
            "+    {\n" +
            "+    }\n" +
            "+    [Fact]\n" +
            "+    public void WhenHelperExists_ReturnsValue()\n" +
            "+    {\n" +
            "+    }\n" +
            "+}\n";

        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(diff);

        result.Should().Contain("WorkItemDispatchEndpointHelperCoverageTests");
        result.Should().Contain("WhenNoHelper_ReturnsNull");
        result.Should().Contain("WhenHelperExists_ReturnsValue");
        // TODO: The MethodDeclaration regex has a known false-positive bug (documented in
        // DroppedIdentifierExtractor.cs) where a method with a generic return type such as
        // "public static IReadOnlyList<string> DoWork(" causes the regex to capture the return-type
        // token ("IReadOnlyList") instead of the method name ("DoWork"). No test exercises this path,
        // so a regression when the TODO is fixed would go undetected. Add a test case with a method
        // using a generic return type to lock in the correct behavior once the TODO is resolved.
    }

    // ── ExtractFromMergeResult ───────────────────────────────────────────────

    [Fact]
    public void ExtractFromMergeResult_EmptyForceResolvedContext_ReturnsEmptyDict()
    {
        var mergeResult = new MergeResult
        {
            Success = true,
            HasConflicts = true,
            ForceResolved = true,
            ConflictFiles = [],
            ForceResolvedContext = []
        };

        var result = DroppedIdentifierExtractor.ExtractFromMergeResult(mergeResult);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractFromMergeResult_FileWithNoIdentifiers_ExcludedFromDict()
    {
        var mergeResult = new MergeResult
        {
            Success = true,
            HasConflicts = true,
            ForceResolved = true,
            ConflictFiles = ["src/Config.json"],
            ForceResolvedContext =
            [
                new ForceResolvedFileContext
                {
                    Path = "src/Config.json",
                    BranchChange = "+{\"key\": \"value\"}\n",
                    BaseChange = ""
                }
            ]
        };

        var result = DroppedIdentifierExtractor.ExtractFromMergeResult(mergeResult);

        // JSON file has no C# type declarations — should produce no identifiers
        result.Should().NotContainKey("src/Config.json");
    }

    [Fact]
    public void ExtractFromMergeResult_MultipleFiles_AllProcessed()
    {
        var mergeResult = new MergeResult
        {
            Success = true,
            HasConflicts = true,
            ForceResolved = true,
            ConflictFiles = ["src/A.cs", "src/B.cs"],
            ForceResolvedContext =
            [
                new ForceResolvedFileContext
                {
                    Path = "src/A.cs",
                    BranchChange = "+public class ServiceA\n{\n}\n",
                    BaseChange = ""
                },
                new ForceResolvedFileContext
                {
                    Path = "src/B.cs",
                    BranchChange = "+public class ServiceB\n{\n}\n",
                    BaseChange = ""
                }
            ]
        };

        var result = DroppedIdentifierExtractor.ExtractFromMergeResult(mergeResult);

        result.Should().ContainKey("src/A.cs");
        result["src/A.cs"].Should().Contain("ServiceA");
        result.Should().ContainKey("src/B.cs");
        result["src/B.cs"].Should().Contain("ServiceB");
    }

    [Fact]
    public void ExtractFromMergeResult_NullMergeResult_Throws()
    {
        var act = () => DroppedIdentifierExtractor.ExtractFromMergeResult(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── ExtractTestMethodIdentifiers branch coverage ─────────────────────────

    [Fact]
    public void ExtractAddedIdentifiers_TestAttribute_FollowedByKeywordMethodName_KeywordNotAdded()
    {
        // The matched method name "get" is a keyword — should be filtered out.
        var diff = "+    [Fact]\n+    public void get()\n+    {\n+    }\n";

        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(diff);

        result.Should().NotContain("get",
            "keyword method names must be filtered by IsKeyword");
    }

    [Fact]
    public void ExtractAddedIdentifiers_TestAttribute_FollowedByNonAddedLine_MethodNotExtracted()
    {
        // After the test attribute the only following line is a context line (no '+' prefix).
        // The window loop should skip it and find no method name to add.
        var diff = "+    [Fact]\n     public void SomeMethod()\n    {\n    }\n";

        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(diff);

        result.Should().NotContain("SomeMethod",
            "context lines (no leading '+') must not contribute method names");
    }

    [Fact]
    public void ExtractAddedIdentifiers_TestAttribute_FollowedByLineNotMatchingTestMethodPattern_NothingExtracted()
    {
        // After the test attribute the added line starts with '+' but does not match the
        // TestMethodName pattern (e.g. it is a comment line with a '+' prefix).
        var diff = "+    [Fact]\n+    // just a comment\n";

        var result = DroppedIdentifierExtractor.ExtractAddedIdentifiers(diff);

        // No identifier should come from the comment line.
        result.Should().NotContain("just");
    }
}
