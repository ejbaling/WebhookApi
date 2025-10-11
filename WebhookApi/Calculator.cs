namespace WebhookAPi;
public class Calculator
{
    public int MultiplyNumbers(int a, int b)
    {
        return checked(a * b);
    }

    // added to satisfy NUnitExample.Tests::CalculatorTests.AddNumbers_WhenGivenTwoIntegers_ReturnsCorrectSum
    public int AddNumbers(int a, int b)
    {
        return checked(a + b);
    }
}

