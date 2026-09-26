using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

public class BranchNameTests
{
    [Fact]
    public void ImplicitConversion_FromString_ProducesCorrectValue()
    {
        BranchName branch = "feature/foo";

        branch.Value.Should().Be("feature/foo");
    }

    [Fact]
    public void ImplicitConversion_FromNull_ThrowsArgumentException()
    {
        var act = () => { BranchName branch = (string)null!; };

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ImplicitConversion_FromEmpty_ThrowsArgumentException()
    {
        var act = () => { BranchName branch = string.Empty; };

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ImplicitConversion_ToString_ReturnsValue()
    {
        BranchName branch = "main";

        string result = branch;

        result.Should().Be("main");
    }

    [Fact]
    public void ToString_ReturnsInnerValue()
    {
        var branch = new BranchName("feature/bar");

        branch.ToString().Should().Be("feature/bar");
    }

    [Fact]
    public void Equality_SameValue_AreEqual()
    {
        BranchName a = "main";
        BranchName b = "main";

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Equality_DifferentValues_AreNotEqual()
    {
        BranchName a = "main";
        BranchName b = "develop";

        a.Should().NotBe(b);
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Default_HasNullValue()
    {
        var branch = default(BranchName);

        branch.Value.Should().BeNull();
    }
}
