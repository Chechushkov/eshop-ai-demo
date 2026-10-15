using System.Text.Json;
using System.Text.Json.Nodes;

namespace eShop.SupportApi;

// User identity, token, selected order and write permission come from the C# context,
// never from model-generated arguments.
public sealed class SupportTools(IKnowledgeSearch knowledge, IOrderReader orders,
    ITicketStore tickets, RunContext context)
{
    public async Task<IReadOnlyList<KnowledgeHit>> SearchKnowledgeAsync(string query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        context.Log("tool SearchKnowledge");
        var queries = KnowledgeQueryPlanner.Build(query);
        context.Log($"rag query_plan queries={queries.Count}");

        var candidates = new List<KnowledgeHit>();
        for (int index = 0; index < queries.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var found = await knowledge.SearchAsync(queries[index], ct);
            candidates.AddRange(found);
            context.Log($"rag query {index + 1}/{queries.Count} hits={found.Count}");
        }

        ct.ThrowIfCancellationRequested();

        // Preserve each question's results instead of truncating the union to three.
        // Duplicate chunks keep their highest observed similarity score.
        var hits = candidates
            .GroupBy(hit => hit.Id, StringComparer.Ordinal)
            .Select(group => group.MaxBy(hit => hit.Score)!)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Id, StringComparer.Ordinal)
            .ToArray();
        context.AddSources(hits);
        return hits;
    }

    public async Task<OrderResult> GetOrderStatusAsync(CancellationToken ct)
    {
        context.Log("tool GetOrderStatus");
        if (context.Request.OrderId is not int id || id <= 0)
            return context.Order = new(false, null, "Заказ не выбран.");
        return context.Order = await orders.GetAsync(id, context.AccessToken, ct);
    }

    public async Task<TicketResult> CreateSupportTicketAsync(string summary, CancellationToken ct)
    {
        context.Log("tool CreateSupportTicket");
        if (!context.Request.AllowTicket)
            return context.Ticket = new(false, null, "Покупатель не разрешил создание заявки.");
        if (context.Request.RequestId == Guid.Empty)
            return context.Ticket = new(false, null, "Нет requestId для идемпотентности.");
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 1000)
            return context.Ticket = new(false, null, "Тема заявки должна содержать 1..1000 символов.");
        // Recheck ownership before writing; the model cannot select another buyer's order.
        var order = await GetOrderStatusAsync(ct);
        if (!order.Found || order.Order is null)
            return context.Ticket = new(false, null, "Заказ не найден среди заказов текущего покупателя.");
        var ticket = await tickets.CreateAsync(context.UserId, order.Order.OrderNumber,
            context.Request.RequestId, summary, ct);
        return context.Ticket = new(true, ticket, "Заявка сохранена в учебной поддержке eShop.");
    }

    public async Task<string> InvokeAsync(string name, string arguments, CancellationToken ct)
    {
        var args = JsonNode.Parse(arguments)?.AsObject() ?? throw new ArgumentException("Tool arguments are missing.");
        object result = name switch
        {
            "SearchKnowledge" => await SearchKnowledgeAsync(args["query"]!.GetValue<string>(), ct),
            "GetOrderStatus" => await GetOrderStatusAsync(ct),
            "CreateSupportTicket" => await CreateSupportTicketAsync(args["summary"]!.GetValue<string>(), ct),
            _ => throw new ArgumentException("Unknown tool.")
        };
        return JsonSerializer.Serialize(result);
    }

    public static JsonArray Definitions() => JsonNode.Parse("""
        [
          {"type":"function","name":"SearchKnowledge",
           "description":"Найти правила учебного магазина. Используй перед ответом о возврате, доставке или гарантии.",
           "strict":true,
           "parameters":{"type":"object","properties":{"query":{"type":"string"}},
                         "required":["query"],"additionalProperties":false}},
          {"type":"function","name":"GetOrderStatus",
           "description":"Получить статус выбранного заказа текущего покупателя. Не вычисляет задержку или дату доставки.",
           "strict":true,
           "parameters":{"type":"object","properties":{},"required":[],"additionalProperties":false}},
          {"type":"function","name":"CreateSupportTicket",
           "description":"Создать заявку по выбранному заказу, только если покупатель явно просит её или нужна помощь человека. AllowTicket — разрешение, не команда. C# проверяет владение и текущее разрешение.",
           "strict":true,
           "parameters":{"type":"object","properties":{"summary":{"type":"string"}},
                         "required":["summary"],"additionalProperties":false}}
        ]
        """)!.AsArray();
}
