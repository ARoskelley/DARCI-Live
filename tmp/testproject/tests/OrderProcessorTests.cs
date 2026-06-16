using Xunit;

namespace TestProject.Tests;

public class OrderProcessorTests
{
    [Fact]
    public void Validate_ReturnsFalse_WhenOrderIsNull()
    {
        var processor = new OrderProcessor(null!, null!);
        Assert.False(processor.Validate(null!));
    }

    [Fact]
    public void Validate_ReturnsFalse_WhenNoItems()
    {
        var processor = new OrderProcessor(null!, null!);
        var order = new Order { CustomerId = "C1", TotalAmount = 10m };
        Assert.False(processor.Validate(order));
    }

    [Fact]
    public void Validate_ReturnsTrue_WhenOrderIsValid()
    {
        var processor = new OrderProcessor(null!, null!);
        var order = new Order
        {
            CustomerId = "C1",
            Items = new() { new OrderItem { ProductId = "P1", Quantity = 1, UnitPrice = 9.99m } },
            TotalAmount = 9.99m
        };
        Assert.True(processor.Validate(order));
    }

    [Fact]
    public void ProcessAsync_ReturnsFailure_WhenTotalAmountDoesNotMatch()
    {
        var processor = new OrderProcessor(null!, null!);
        var order = new Order
        {
            CustomerId = "C1",
            Items = new() { new OrderItem { ProductId = "P1", Quantity = 1, UnitPrice = 9.99m } },
            TotalAmount = 10m
        };
        var result = processor.ProcessAsync(order).Result;
        Assert.Equal(OrderResult.Failure("Amount mismatch"), result);
    }
}