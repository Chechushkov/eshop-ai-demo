using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace eShop.SupportApi;

public sealed class OrderingClient(HttpClient http) : IOrderReader
{
    public async Task<OrderResult> GetAsync(int orderId, string accessToken, CancellationToken ct)
    {
        // В этой версии eShop список фильтруется по sub покупателя.
        // Не используем GET /api/orders/{id}, который сам не проверяет владельца.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/orders/?api-version=1.0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new UpstreamException($"Ordering.API HTTP {(int)response.StatusCode}.");
        var orders = await response.Content.ReadFromJsonAsync<OrderInfo[]>(cancellationToken: ct) ?? [];
        var order = orders.SingleOrDefault(x => x.OrderNumber == orderId);
        return order is null
            ? new(false, null, "Заказ не найден среди заказов текущего покупателя.")
            : new(true, order, "Статус получен из Ordering.API.");
    }
}
