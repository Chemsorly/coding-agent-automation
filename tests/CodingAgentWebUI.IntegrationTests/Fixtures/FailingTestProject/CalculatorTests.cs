using Xunit;

namespace FailingTestProject;

public class CalculatorTests
{
    [Fact]
    public void Add_ReturnsSumOfTwoNumbers() => Assert.Equal(5, Calculator.Add(2, 3));

    [Fact]
    public void Add_DeliberatelyFails() => Assert.Equal(99, Calculator.Add(2, 3));
}
