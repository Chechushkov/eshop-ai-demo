using System.Text.Json.Nodes;

namespace eShop.SupportApi;

public sealed record OpenAiSettings(
    string ApiKey,
    string Model,
    string EmbeddingModel)
{
    public const int Dimensions = 1536;
}

public sealed record SupportRequest(
    string Question,
    int? OrderId,
    bool AllowTicket,
    Guid RequestId,
    string Mode = "workflow",
    bool DemoRetry = false,
    Guid? ConversationId = null);

public sealed record OrderInfo(
    int OrderNumber,
    DateTime Date,
    string Status,
    decimal Total);

public sealed record OrderResult(
    bool Found,
    OrderInfo? Order,
    string Message);

public sealed record KnowledgeHit(
    string Id,
    string Title,
    string Text,
    double Score);

public sealed record SupportTicket(
    string Id,
    int OrderId,
    string Summary,
    DateTime CreatedAt);

public sealed record TicketResult(
    bool Created,
    SupportTicket? Ticket,
    string Message);

public sealed record TicketDecision(bool NeedsTicket, string Reason);

public sealed record SupportReply(
    string Answer,
    IReadOnlyList<KnowledgeHit> Sources,
    OrderResult? Order,
    TicketResult? Ticket,
    string[] Trace,
    bool UsedFallback,
    Guid? ConversationId = null,
    SupportMetrics? Metrics = null,
    SupportBehavior? Behavior = null);

// Assigned by application code, never extracted from model text.
// Null on older replies means these observations were not recorded.
public sealed record SupportBehavior(
    string[] AutoCompletedActions,
    bool ReplyRecomposed);

// Metrics for a single message.
// A null token count means usage is unknown.
// IsReplay=true identifies a reply loaded from storage.
public sealed record SupportMetrics(
    long ElapsedMs,
    int ModelCalls,
    int EmbeddingCalls,
    long? InputTokens,
    long? OutputTokens,
    long? EmbeddingTokens,
    bool IsReplay,
    string Model,
    string EmbeddingModel);

public sealed record ConversationTurn(
    SupportRequest Request,
    SupportReply Reply,
    DateTime CreatedAt);

public sealed record ConversationView(
    Guid Id,
    int? OrderId,
    ConversationTurn[] Turns);

public sealed record ConversationSummary(
    Guid Id,
    string Title,
    int? OrderId,
    DateTime UpdatedAt);

public sealed class RunContext(
    SupportRequest request,
    string userId,
    string accessToken,
    IReadOnlyList<ConversationTurn>? history = null)
{
    public SupportRequest Request { get; } = request;
    public string UserId { get; } = userId;
    public string AccessToken { get; } = accessToken;

    public IReadOnlyList<ConversationTurn> History { get; } =
        history ?? [];

    public OrderResult? Order { get; set; }
    public TicketResult? Ticket { get; set; }
    public TicketDecision? TicketDecision { get; set; }

    public List<KnowledgeHit> Sources { get; } = [];
    public List<string> Trace { get; } = [];
    public List<string> AutoCompletedActions { get; } = [];
    public bool ReplyRecomposed { get; set; }
    public bool KnowledgeSearchCompleted { get; set; }
    public bool HasNoRelevantKnowledge => KnowledgeSearchCompleted && Sources.Count == 0;

    public void Log(string step)
    {
        Trace.Add(step);
        Console.WriteLine($"[support] {step}");
    }

    public void LogRequest()
    {
        Log(
            $"request id={Request.RequestId:N} " +
            $"conversation={Request.ConversationId?.ToString("N") ?? "none"} " +
            $"historyTurns={History.Count} mode={Request.Mode} " +
            $"order={Request.OrderId?.ToString() ?? "none"} " +
            $"allowTicket={Request.AllowTicket} " +
            $"demoRetry={Request.DemoRetry}");
    }

    public void LogValidation(string stage, string[] errors)
    {
        Log($"{stage}: errors={errors.Length}");

        foreach (var error in errors)
            Log($"{stage}: {error}");
    }

    public void AddSources(IEnumerable<KnowledgeHit> hits)
    {
        foreach (var hit in hits)
        {
            if (!Sources.Any(x => x.Id == hit.Id))
                Sources.Add(hit);
        }
    }
}

public interface IKnowledgeSearch
{
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(
        string query,
        CancellationToken ct);
}

public interface IKnowledgeRelevance
{
    Task<IReadOnlyList<KnowledgeHit>> SelectAsync(
        string question,
        string searchQuery,
        IReadOnlyList<KnowledgeHit> candidates,
        IReadOnlyList<ConversationTurn> history,
        CancellationToken ct);
}

public interface IOrderReader
{
    Task<OrderResult> GetAsync(
        int orderId,
        string accessToken,
        CancellationToken ct);
}

public interface ITicketStore
{
    Task<SupportTicket> CreateAsync(
        string userId,
        int orderId,
        Guid requestId,
        string summary,
        CancellationToken ct);
}

public interface IResponsesModel
{
    // Null allows the agent to answer or choose a tool. Decision steps name the
    // required function explicitly; a prompt alone does not enforce a tool call.
    Task<JsonObject> RespondAsync(
        string instructions,
        IReadOnlyList<JsonNode> input,
        JsonArray? tools,
        CancellationToken ct,
        string? requiredFunction = null);
}

public sealed record ReplyDraft(
    string Text,
    int Attempt,
    string[] Errors)
{
    public bool NeedsRetry => Errors.Length > 0 && Attempt < 2;
}

public sealed class UpstreamException(string message)
    : Exception(message);
