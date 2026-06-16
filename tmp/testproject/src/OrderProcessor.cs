namespace TestProject;

public class OrderProcessor
{
    private readonly IOrderRepository _repo;
    private readonly IPaymentGateway _gateway;

    public OrderProcessor(IOrderRepository repo, IPaymentGateway gateway)
    {
        _repo = repo;
        _gateway = gateway;
    }

    public bool Validate(Order order)
    {
        if (order == null) return false;
        if (string.IsNullOrWhiteSpace(order.CustomerId)) return false;
        if (order.Items == null || order.Items.Count == 0) return false;
        return order.TotalAmount > 0;
    }

    public async Task<OrderResult> ProcessAsync(Order order)
    {
        if (!Validate(order))
            return OrderResult.Failure("Validation failed");

        // Add input validation for TotalAmount
        decimal calculatedTotal = order.Items.Sum(item => item.Quantity * item.UnitPrice);
        if (order.TotalAmount != calculatedTotal)
            return OrderResult.Failure("Amount mismatch");

        var payment = await _gateway.ChargeAsync(order.CustomerId, order.TotalAmount);
        if (!payment.Success)
            return OrderResult.Failure(payment.Error);

        await _repo.SaveAsync(order);
        return OrderResult.Success(order.Id);
    }

    public Order? FindById(string id)
    {
        return _repo.FindById(id);
    }
}