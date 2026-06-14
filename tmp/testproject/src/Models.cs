namespace TestProject;

public class Order
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string CustomerId { get; set; } = "";
    public List<OrderItem> Items { get; set; } = new();
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class OrderItem
{
    public string ProductId { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public class OrderResult
{
    public bool Success { get; set; }
    public string? OrderId { get; set; }
    public string? Error { get; set; }

    public static OrderResult Success(string orderId) => new() { Success = true, OrderId = orderId };
    public static OrderResult Failure(string error) => new() { Success = false, Error = error };
}

public class PaymentRequest
{
    public string CardNumber { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
}

public class PaymentResult
{
    public bool Success { get; set; }
    public string? TransactionId { get; set; }
    public string? Error { get; set; }
}

public interface IOrderRepository
{
    Task SaveAsync(Order order);
    Order? FindById(string id);
}

public interface IPaymentGateway
{
    bool Validate(PaymentRequest request);
    Task<PaymentResult> ChargeAsync(string customerId, decimal amount);
    Task<PaymentResult> RefundAsync(string transactionId);
}
