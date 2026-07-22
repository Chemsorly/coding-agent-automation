using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class WorkspacePathTests
{
    [Fact]
    public void ImplicitConversion_FromString_ProducesCorrectValue()
    {
        WorkspacePath path = "/tmp/workspace-1";

        path.Value.Should().Be("/tmp/workspace-1");
    }

    [Fact]
    public void ImplicitConversion_FromNull_ThrowsArgumentNullException()
    {
        var act = () => { WorkspacePath path = (string)null!; };

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ImplicitConversion_FromEmpty_ThrowsArgumentException()
    {
        var act = () => { WorkspacePath path = string.Empty; };

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ImplicitConversion_ToString_ReturnsValue()
    {
        WorkspacePath path = "/tmp/workspace-2";

        string result = path;

        result.Should().Be("/tmp/workspace-2");
    }

    [Fact]
    public void ToString_ReturnsInnerValue()
    {
        var path = new WorkspacePath("/tmp/workspace-3");

        path.ToString().Should().Be("/tmp/workspace-3");
    }

    [Fact]
    public void Equality_ByValue()
    {
        WorkspacePath a = "/tmp/workspace-4";
        WorkspacePath b = "/tmp/workspace-4";

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Inequality_DifferentValues()
    {
        WorkspacePath a = "/tmp/workspace-a";
        WorkspacePath b = "/tmp/workspace-b";

        a.Should().NotBe(b);
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Default_HasNullValue()
    {
        var path = default(WorkspacePath);

        path.Value.Should().BeNull();
    }
}
