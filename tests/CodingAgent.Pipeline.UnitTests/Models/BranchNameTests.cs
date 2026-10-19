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
    public void ImplicitConversion_FromDefault_ThrowsArgumentException()
    {
        var act = () => { string _ = default(BranchName); };

        act.Should().Throw<ArgumentException>();
    }

    // TODO [WARNING]: No test for whitespace-only input through the implicit string → BranchName
    // operator. ArgumentException.ThrowIfNullOrEmpty does not reject strings like "   ", so a
    // whitespace-only branch name would be silently accepted. Add a test (and, if desired, switch to
    // ArgumentException.ThrowIfNullOrWhiteSpace) to specify and lock in the intended boundary.

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
    public void Constructor_WithNull_ThrowsArgumentNullException()
    {
        var act = () => new BranchName(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithEmpty_ThrowsArgumentException()
    {
        var act = () => new BranchName(string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    // TODO [WARNING]: Default_HasNullValue confirms that default(BranchName).Value is null, but there
    // is no corresponding test verifying that passing a default(BranchName) through the implicit string
    // operator fails early with a clear exception. After the BranchName → string operator guard was
    // added, default(BranchName) implicitly converted to string will throw; a test for that path would
    // document the intended failure mode. The new BranchName(null) path is now covered by
    // Constructor_WithNull_ThrowsArgumentNullException above.
    [Fact]
    public void Default_HasNullValue()
    {
        var branch = default(BranchName);

        branch.Value.Should().BeNull();
    }
}
