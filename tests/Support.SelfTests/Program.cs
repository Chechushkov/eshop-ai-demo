using eShop.SupportApi;
using System.Net;
using System.Text.Json.Nodes;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    checks++;
}
SupportRequest Request(bool allow = false, bool retry = false, int? orderId = 17) =>
    new("Проверь заказ и правила доставки.", orderId, allow, Guid.NewGuid(), "workflow", retry);
var knowledge = new FakeKnowledge();
var orders = new FakeOrders();
var tickets = new FakeTickets();
RunContext Context(SupportRequest request) => new(request, "buyer-test", "token-fixture");
SupportTools Tools(RunContext context) => new(knowledge, orders, tickets, context);

// Authorization and idempotency are tested with fake dependencies.
var blocked = Context(Request());
var denied = await Tools(blocked).CreateSupportTicketAsync("Уточнить доставку", default);
Check(!denied.Created && tickets.Writes == 0, "Ticket creation is blocked without permission.");
var foreign = Context(Request(true, orderId: 999));
Check(!(await Tools(foreign).GetOrderStatusAsync(default)).Found, "An unavailable order is not returned.");
Check(!(await Tools(foreign).CreateSupportTicketAsync("Доставка", default)).Created,
    "A ticket is not created for an unavailable order.");
var owned = Context(Request(true));
var first = await Tools(owned).CreateSupportTicketAsync("Доставка", default);
var second = await Tools(owned).CreateSupportTicketAsync("Доставка", default);
Check(first.Ticket?.Id == second.Ticket?.Id && tickets.Writes == 1, "Repeating the same request does not create a second ticket.");

// Keep Russian fixtures to exercise the demo language; diagnostics are English.
owned.AddSources(await knowledge.SearchAsync("доставка", default));
Check(ReplyComposer.Validate("Нет ссылки.", owned).Length > 0, "A reply without a required citation is rejected.");
Check(ReplyComposer.Validate("[invented#001] T-00000000000000000000000000000000", owned).Length > 0,
    "Invented source and ticket IDs are rejected.");
string valid = "Правило [support-delivery#001]. Заявка: " + first.Ticket!.Id;
Check(ReplyComposer.Validate(valid, owned).Length == 0, "A reply with confirmed citations and a ticket ID passes validation.");
Check(ReplyComposer.Finish("", owned, true).Answer.Contains(first.Ticket.Id), "The fallback preserves the created ticket ID.");

// Exercise the normal path and the deliberately rejected first draft.
foreach (bool retry in new[] { false, true })
{
    var context = Context(Request(true, retry));
    var model = new FakeModel(() =>
        "Правила [support-delivery#001]. Заявка: " + context.Ticket!.Ticket!.Id);
    var workflow = new SupportWorkflow(new ReplyComposer(model));
    var result = await workflow.RunAsync(context, Tools(context), default);
    Check(!result.UsedFallback, "The workflow produces a validated reply.");
    Check(model.Calls == (retry ? 2 : 1), "Demo retry allows only one additional model call.");
    Check(result.Trace.Contains("workflow finish OK"), "The workflow reaches the finish executor.");
}
var fallbackContext = Context(Request());
var fallbackModel = new FakeModel(() => "Ответ без источников.");
var fallbackReply = await new SupportWorkflow(new ReplyComposer(fallbackModel))
    .RunAsync(fallbackContext, Tools(fallbackContext), default);
Check(fallbackReply.UsedFallback && fallbackModel.Calls == 2, "Two invalid drafts result in a fallback.");
var noOrder = Context(Request(orderId: null));
var noOrderModel = new FakeModel(() => "Правила [support-delivery#001].");
var general = await new SupportWorkflow(new ReplyComposer(noOrderModel)).RunAsync(noOrder, Tools(noOrder), default);
Check(general.Order is null && !general.UsedFallback, "A general RAG question works without a selected order.");

// Scripted Responses API items verify tool execution without a real model.
var agentContext = Context(Request());
var script = new ScriptModel([
    JsonNode.Parse("""{"status":"completed","output":[{"type":"function_call","call_id":"call-1","name":"GetOrderStatus","arguments":"{}"}]}""")!.AsObject(),
    JsonNode.Parse("""{"status":"completed","output":[{"type":"function_call","call_id":"call-2","name":"SearchKnowledge","arguments":"{\"query\":\"доставка\"}"}]}""")!.AsObject(),
    FakeModel.Text("Заказ проверен. Правила [support-delivery#001].")
]);
var agent = await new SupportAgent(script, new ReplyComposer(script))
    .RunAsync(agentContext, Tools(agentContext), default);
Check(agentContext.Order is { Found: true }, "A structured tool call executes the C# tool.");
Check(script.LastInput!.Any(x => x["type"]?.GetValue<string>() == "function_call_output"),
    "The tool result is returned to the model.");
Check(!agent.UsedFallback, "The agent produces a validated reply.");

// The handler returns a fixture; no network request leaves this process.
using var http = new HttpClient(new CaptureHandler(
    """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"OK"}]}]}"""))
    { BaseAddress = new Uri("https://fixture.invalid/v1/") };
var api = new OpenAiApi(http, new("fixture-not-a-key", "gpt-5.4-mini", "text-embedding-3-small"));
var response = await api.RespondAsync("test", [OpenAiApi.UserMessage("test")], SupportTools.Definitions(), default);
Check(OpenAiApi.OutputText(response) == "OK", "The Responses API parser extracts output text.");
Check(KnowledgeRepository.Split(new string('x', 1700)).Length == 2, "Knowledge splitting creates overlapping chunks.");
// PostgreSQL vector text must remain valid under a non-English culture.
var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
try
{
    System.Globalization.CultureInfo.CurrentCulture = new("de-DE");
    Check(KnowledgeRepository.VectorText([0.1f, 0.2f]) == "[0.100000001,0.200000003]",
        "Vector formatting uses a decimal point regardless of culture.");
}
finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }

// A token cancelled before execution must stop the workflow.
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    var state = Context(Request());
    await new SupportWorkflow(new ReplyComposer(new FakeModel(() => "unused")))
        .RunAsync(state, Tools(state), cancelled.Token);
    throw new InvalidOperationException("Cancellation was ignored.");
}
catch (OperationCanceledException) { checks++; }

Console.WriteLine($"OK: {checks} offline checks passed. No external APIs or PostgreSQL were called.");

sealed class FakeKnowledge : IKnowledgeSearch
{
    public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<KnowledgeHit>>([new("support-delivery#001", "Доставка", "Учебное правило.", 0.8)]);
}
sealed class FakeOrders : IOrderReader
{
    public Task<OrderResult> GetAsync(int id, string token, CancellationToken ct) =>
        Task.FromResult(id == 17 ? new OrderResult(true, new(17, DateTime.UtcNow, "Paid", 100), "OK") :
            new OrderResult(false, null, "Not found"));
}
sealed class FakeTickets : ITicketStore
{
    private readonly Dictionary<(string, int, Guid), SupportTicket> items = [];
    public int Writes { get; private set; }
    public Task<SupportTicket> CreateAsync(string user, int order, Guid request, string summary, CancellationToken ct)
    {
        var key = (user, order, request);
        if (!items.TryGetValue(key, out var value))
        {
            value = new("T-" + Guid.NewGuid().ToString("N"), order, summary, DateTime.UtcNow);
            items.Add(key, value);
            Writes++;
        }
        return Task.FromResult(value);
    }
}
sealed class FakeModel(Func<string> text) : IResponsesModel
{
    public int Calls { get; private set; }
    public Task<JsonObject> RespondAsync(string instructions, IReadOnlyList<JsonNode> input, JsonArray? tools, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(Text(text()));
    }
    public static JsonObject Text(string value) => new()
    {
        ["status"] = "completed",
        ["output"] = new JsonArray(new JsonObject
        {
            ["type"] = "message",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = value })
        })
    };
}
sealed class ScriptModel(JsonObject[] replies) : IResponsesModel
{
    private int index;
    public IReadOnlyList<JsonNode>? LastInput { get; private set; }
    public Task<JsonObject> RespondAsync(string instructions, IReadOnlyList<JsonNode> input, JsonArray? tools, CancellationToken ct)
    {
        LastInput = input.ToArray();
        return Task.FromResult(replies[index++]);
    }
}
sealed class CaptureHandler(string response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response,
            System.Text.Encoding.UTF8, "application/json") });
}
