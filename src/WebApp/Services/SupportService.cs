using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace eShop.WebApp.Services;

public sealed class SupportService(HttpClient http)
{
    public async Task<SupportAnswer> AskAsync(
        SupportQuestion question,
        CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            "/api/support/ask",
            question,
            ct);

        await EnsureSuccessAsync(response, ct);

        return await response.Content
            .ReadFromJsonAsync<SupportAnswer>(
                cancellationToken: ct)
            ?? throw new InvalidOperationException(
                "Пустой ответ Support.API.");
    }

    public async Task<SupportConversationSummary[]> GetConversationsAsync(
        CancellationToken ct = default)
    {
        using var response = await http.GetAsync(
            "/api/support/conversations",
            ct);

        await EnsureSuccessAsync(response, ct);

        return await response.Content
            .ReadFromJsonAsync<SupportConversationSummary[]>(
                cancellationToken: ct)
            ?? throw new InvalidOperationException(
                "Пустой список разговоров Support.API.");
    }

    public async Task<SupportConversationView?> GetConversationAsync(
        Guid id,
        CancellationToken ct = default)
    {
        using var response = await http.GetAsync(
            $"/api/support/conversations/{id:D}",
            ct);

        if (response.StatusCode ==
            System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct);

        return await response.Content
            .ReadFromJsonAsync<SupportConversationView>(
                cancellationToken: ct)
            ?? throw new InvalidOperationException(
                "Пустой ответ истории Support.API.");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        string message =
            $"Support.API HTTP {(int)response.StatusCode}.";

        try
        {
            var problem = await response.Content
                .ReadFromJsonAsync<JsonObject>(
                    cancellationToken: ct);

            message = problem?["detail"]?.GetValue<string>()
                ?? message;
        }
        catch (System.Text.Json.JsonException)
        {
        }

        throw new InvalidOperationException(message);
    }
}

public sealed record SupportQuestion(
    string Question,
    int? OrderId,
    bool AllowTicket,
    Guid RequestId,
    string Mode,
    bool DemoRetry,
    Guid? ConversationId = null);

public sealed record SupportSource(
    string Id,
    string Title,
    string Text,
    double Score);

public sealed record SupportOrder(
    int OrderNumber,
    DateTime Date,
    string Status,
    decimal Total);

public sealed record SupportOrderResult(
    bool Found,
    SupportOrder? Order,
    string Message);

public sealed record SupportTicketInfo(
    string Id,
    int OrderId,
    string Summary,
    DateTime CreatedAt);

public sealed record SupportTicketResult(
    bool Created,
    SupportTicketInfo? Ticket,
    string Message);

public sealed record SupportAnswer(
    string Answer,
    SupportSource[] Sources,
    SupportOrderResult? Order,
    SupportTicketResult? Ticket,
    string[] Trace,
    bool UsedFallback,
    Guid? ConversationId = null,
    SupportRequestMetrics? Metrics = null);

public sealed record SupportRequestMetrics(
    long ElapsedMs,
    int ModelCalls,
    int EmbeddingCalls,
    long? InputTokens,
    long? OutputTokens,
    long? EmbeddingTokens,
    bool IsReplay,
    string Model,
    string EmbeddingModel);

public sealed record SupportConversationTurn(
    SupportQuestion Request,
    SupportAnswer Reply,
    DateTime CreatedAt);

public sealed record SupportConversationView(
    Guid Id,
    int? OrderId,
    SupportConversationTurn[] Turns);

public sealed record SupportConversationSummary(
    Guid Id,
    string Title,
    int? OrderId,
    DateTime UpdatedAt);