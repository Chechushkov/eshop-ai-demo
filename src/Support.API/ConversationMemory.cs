using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace eShop.SupportApi;

public static class ConversationMemory
{
    public const int ContextTurns = 6;
    public const int DisplayTurns = 50;
    public const int RecentConversations = 50;

    public static string CreateTitle(string question)
    {
        string title = string.Join(
            " ",
            question.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

        if (title.Length == 0)
            return "Untitled conversation";

        if (title.Length <= 100)
            return title;

        int length = char.IsHighSurrogate(title[98]) ? 98 : 99;

        return title[..length] + "…";
    }

    public static List<JsonNode> Messages(
        IReadOnlyList<ConversationTurn> history)
    {
        var messages = new List<JsonNode>();

        foreach (var turn in history.TakeLast(ContextTurns))
        {
            messages.Add(
                OpenAiApi.UserMessage(turn.Request.Question));

            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = Limit(turn.Reply.Answer, 4000)
            });
        }

        return messages;
    }

    public static SupportTicket[] KnownTickets(
        IReadOnlyList<ConversationTurn> history) => history
        .Where(x => x.Reply.Ticket is
        {
            Created: true,
            Ticket: not null
        })
        .Select(x => x.Reply.Ticket!.Ticket!)
        .DistinctBy(x => x.Id)
        .ToArray();

    public static string RequestHash(SupportRequest request) =>
        Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(request))));

    public static string Limit(string text, int max) =>
        text.Length <= max ? text : text[..max];
}

public interface IConversationStore
{
    Task<IConversationLease> OpenAsync(
        SupportRequest request,
        string userId,
        CancellationToken ct);

    Task<ConversationView?> GetAsync(
        Guid conversationId,
        string userId,
        CancellationToken ct);

    Task<ConversationSummary[]> ListAsync(
        string userId,
        CancellationToken ct);
}

public interface IConversationLease : IAsyncDisposable
{
    IReadOnlyList<ConversationTurn> History { get; }

    SupportReply? CachedReply { get; }

    Task SaveAsync(
        SupportReply reply,
        CancellationToken ct);
}

public sealed class ConversationNotFoundException()
    : Exception("Conversation not found.");

public sealed class ConversationRequestConflictException()
    : Exception(
        "This requestId has already been used with different parameters. " +
        "Send a new message with a new requestId.");

public sealed class SupportConversationService(
    IConversationStore store,
    SupportAgent agent,
    SupportWorkflow workflow,
    SupportMetricsCollector? metrics = null)
{
    public async Task<SupportReply> AskAsync(
        SupportRequest request,
        string userId,
        string accessToken,
        IKnowledgeSearch knowledge,
        IOrderReader orders,
        ITicketStore tickets,
        CancellationToken ct)
    {
        request = request with
        {
            ConversationId =
                request.ConversationId ?? request.RequestId
        };

        metrics?.Start();

        try
        {
            await using var lease =
                await store.OpenAsync(request, userId, ct);

            // Replays return the saved reply.
            // The model and tools are not called again.
            if (lease.CachedReply is { } cached)
            {
                var measurement =
                    metrics?.Finish(isReplay: true);

                var trace = new List<string>
                {
                    $"conversation replay id={request.ConversationId:N} " +
                    $"request={request.RequestId:N}",

                    "Reply loaded from PostgreSQL: the model and tools " +
                    "were not called again."
                };

                if (measurement is not null)
                {
                    string line =
                        SupportMetricsCollector.Describe(measurement);

                    trace.Add(line);
                    Console.WriteLine($"[support] {line}");
                }

                // Update only the returned reply.
                // Keep the original metrics in the database.
                return cached with
                {
                    Metrics = measurement,
                    Trace = trace.ToArray()
                };
            }

            var context = new RunContext(
                request,
                userId,
                accessToken,
                lease.History);

            var tools = new SupportTools(
                knowledge,
                orders,
                tickets,
                context);

            SupportReply reply = request.Mode == "agent"
                ? await agent.RunAsync(context, tools, ct)
                : await workflow.RunAsync(context, tools, ct);

            var prepared = metrics?.Finish();

            if (prepared is not null)
            {
                context.Log(
                    SupportMetricsCollector.Describe(prepared));
            }

            reply = reply with
            {
                ConversationId = request.ConversationId,
                Metrics = prepared,
                Trace = context.Trace.ToArray()
            };

            // Stop timing before persisting the reply.
            // Store the metrics with the reply in reply_json.
            await lease.SaveAsync(reply, ct);

            return reply;
        }
        finally
        {
            metrics?.Stop();
        }
    }
}