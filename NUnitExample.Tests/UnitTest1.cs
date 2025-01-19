using NUnit.Framework;
using WebhookAPi;

namespace NUnitExample.Tests;

[TestFixture]
public class CalculatorTests
{
    private readonly Calculator _calculator = new Calculator();

    [SetUp]
    public void Setup()
    {
    }

    [Test]  // This method is marked as a test
    public void AddNumbers_WhenGivenTwoIntegers_ReturnsCorrectSum()
    {
        // Arrange
        int a = 2;
        int b = 3;

        // Act
        int result = _calculator.AddNumbers(a, b);

        // Assert
        Assert.That(result, Is.EqualTo(5));
    }
}
