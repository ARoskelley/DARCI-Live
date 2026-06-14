namespace TestProject;

public class PaymentGateway : IPaymentGateway
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public PaymentGateway(HttpClient http, string apiKey)
    {
        _http = http;
        _apiKey = apiKey;
    }

    public bool Validate(PaymentRequest request)
    {
        if (request == null) return false;
        if (string.IsNullOrWhiteSpace(request.CardNumber)) return false;
        return request.Amount > 0;
    }

    public async Task<PaymentResult> ChargeAsync(string customerId, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            return new PaymentResult { Success = false, Error = "Invalid customer" };

        // Simulated charge
        var response = await _http.PostAsync("/charge", null);
        return new PaymentResult { Success = response.IsSuccessStatusCode };
    }

    public async Task<PaymentResult> RefundAsync(string transactionId)
    {
        var response = await _http.PostAsync($"/refund/{transactionId}", null);
        return new PaymentResult { Success = response.IsSuccessStatusCode };
    }
}
