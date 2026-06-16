using System;
using System.Collections.Generic;

namespace TestProject
{
    public class OrderItem
    {
        public string ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }

    public class Order
    {
        public string CustomerId { get; set; }
        public List<OrderItem> Items { get; set; }
        public decimal TotalAmount { get; set; }
    }

    public enum OrderResult
    {
        Success,
        Failure(string message)
    }

    public class OrderProcessor
    {
        private readonly IOrderRepository _orderRepository;
        private readonly ILogger _logger;

        public OrderProcessor(IOrderRepository orderRepository, ILogger logger)
        {
            _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool Validate(Order order)
        {
            if (order == null) return false;
            if (order.Items == null || order.Items.Count == 0) return true;

            decimal calculatedTotalAmount = 0m;
            foreach (var item in order.Items)
            {
                calculatedTotalAmount += item.Quantity * item.UnitPrice;
            }

            return order.TotalAmount == calculatedTotalAmount;
        }

        public OrderResult ProcessAsync(Order order)
        {
            if (!Validate(order))
            {
                return OrderResult.Failure("Amount mismatch");
            }

            // Simulate processing logic
            _orderRepository.SaveOrder(order);
            _logger.Log($"Processed order for customer {order.CustomerId}");

            return OrderResult.Success;
        }
    }

    public interface IOrderRepository
    {
        void SaveOrder(Order order);
    }

    public class OrderRepository : IOrderRepository
    {
        public void SaveOrder(Order order)
        {
            // Implementation to save order
        }
    }

    public interface ILogger
    {
        void Log(string message);
    }

    public class Logger : ILogger
    {
        public void Log(string message)
        {
            Console.WriteLine(message);
        }
    }
}