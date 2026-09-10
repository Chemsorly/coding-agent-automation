using Xunit;

namespace PassingProject;

public class CalculatorTests
{
    [Fact]
    public void Add_ReturnsSumOfTwoNumbers() => Assert.Equal(5, Calculator.Add(2, 3));
}
