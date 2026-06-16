using Xunit;

namespace TestProject.Tests
{
    public class BehaviorCheck
    {
        [Fact]
        public void ProcessAsync_RejectsOrder_WhenTotalAmountDoesNotMatch()
        {
            var processor = new OrderProcessor(new OrderRepository(), new Logger());
            var order = new Order
            {
                CustomerId = "C1",
                Items = new() { new OrderItem { ProductId = "P1", Quantity = 1, UnitPrice = 9.99m } },
                TotalAmount = 10m
            };
            var result = processor.ProcessAsync(order).Result;
            Assert.Equal(OrderResult.Failure("Amount mismatch"), result);
        }

        [Fact]
        public void ProcessAsync_AcceptsOrder_WhenTotalAmountMatches()
        {
            var processor = new OrderProcessor(new OrderRepository(), new Logger());
            var order = new Order
            {
                CustomerId = "C1",
                Items = new() { new OrderItem { ProductId = "P1", Quantity = 1, UnitPrice = 9.99m } },
                TotalAmount = 9.99m
            };
            var result = processor.ProcessAsync(order).Result;
            Assert.Equal(OrderResult.Success, result);
        }
    }
}