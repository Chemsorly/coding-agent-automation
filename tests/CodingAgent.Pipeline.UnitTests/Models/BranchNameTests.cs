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
    // TODO: This assertion uses Throw<ArgumentNullException>(), but ArgumentException.ThrowIfNullOrEmpty
    // throws ArgumentException (not the derived ArgumentNullException) for a null input on .NET 7+.
    // On .NET 8 this test fails because AwesomeAssertions Throw<T> does exact-type matching.
    // Fix: change Throw<ArgumentNullException>() to Throw<ArgumentException>() to match the actual throw.
    public void ImplicitConversion_FromNull_ThrowsArgumentNullException()
    {
        var act = () => { BranchName branch = (string)null!; };

        act.Should().Throw<ArgumentNullException>();
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
    // TODO: This test constructs BranchName directly via new BranchName("feature/bar"), which bypasses
    // the validated implicit operator. The assertion is behaviourally identical to
    // ImplicitConversion_ToString_ReturnsValue. Consider replacing it with a test that asserts
    // something unique about the direct-constructor path — e.g. that new BranchName(null) does NOT
    // throw (documenting the expected default/direct-construction semantics noted in the class remarks).
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
