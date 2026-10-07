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

var blocked = Context(Request());
var denied = await Tools(blocked).CreateSupportTicketAsync("Уточнить доставку", default);
Check(!denied.Created && tickets.Writes == 0, "заявка без разрешения блокируется");
var foreign = Context(Request(true, orderId: 999));
Check(!(await Tools(foreign).GetOrderStatusAsync(default)).Found, "чужой заказ не найден");
Check(!(await Tools(foreign).CreateSupportTicketAsync("Доставка", default)).Created,
    "для чужого заказа заявка не создаётся");
var owned = Context(Request(true));
var first = await Tools(owned).CreateSupportTicketAsync("Доставка", default);
var second = await Tools(owned).CreateSupportTicketAsync("Доставка", default);
Check(first.Ticket?.Id == second.Ticket?.Id && tickets.Writes == 1, "повтор не создаёт второй тикет");

owned.AddSources(await knowledge.SearchAsync("доставка", default));
Check(ReplyComposer.Validate("Нет ссылки.", owned).Length > 0, "пропущена ссылка");
Check(ReplyComposer.Validate("[invented#001] T-00000000000000000000000000000000", owned).Length > 0,
    "выдуманные источник и тикет");
string valid = "Правило [support-delivery#001]. Заявка: " + first.Ticket!.Id;
Check(ReplyComposer.Validate(valid, owned).Length == 0, "проверенный ответ проходит");
Check(ReplyComposer.Finish("", owned, true).Answer.Contains(first.Ticket.Id), "fallback сохраняет номер");

foreach (bool retry in new[] { false, true })
{
    var context = Context(Request(true, retry));
    var model = new FakeModel(() =>
        "Правила [support-delivery#001]. Заявка: " + context.Ticket!.Ticket!.Id);
    var workflow = new SupportWorkflow(new ReplyComposer(model));
    var result = await workflow.RunAsync(context, Tools(context), default);
    Check(!result.UsedFallback, "workflow имеет конечный результат");
    Check(model.Calls == (retry ? 2 : 1), "учебный retry ограничен одной попыткой");
    Check(result.Trace.Contains("workflow finish OK"), "finish графа выполнен");
}
var fallbackContext = Context(Request());
var fallbackModel = new FakeModel(() => "Ответ без источников.");
var fallbackReply = await new SupportWorkflow(new ReplyComposer(fallbackModel))
    .RunAsync(fallbackContext, Tools(fallbackContext), default);
Check(fallbackReply.UsedFallback && fallbackModel.Calls == 2, "невалидный ответ после двух попыток -> fallback");
var noOrder = Context(Request(orderId: null));
var noOrderModel = new FakeModel(() => "Правила [support-delivery#001].");
var general = await new SupportWorkflow(new ReplyComposer(noOrderModel)).RunAsync(noOrder, Tools(noOrder), default);
Check(general.Order is null && !general.UsedFallback, "общий RAG-вопрос без заказа");

var agentContext = Context(Request());
var script = new ScriptModel([
    JsonNode.Parse("""{"status":"completed","output":[{"type":"function_call","call_id":"call-1","name":"GetOrderStatus","arguments":"{}"}]}""")!.AsObject(),
    JsonNode.Parse("""{"status":"completed","output":[{"type":"function_call","call_id":"call-2","name":"SearchKnowledge","arguments":"{\"query\":\"доставка\"}"}]}""")!.AsObject(),
    FakeModel.Text("Заказ проверен. Правила [support-delivery#001].")
]);
var agent = await new SupportAgent(script, new ReplyComposer(script))
    .RunAsync(agentContext, Tools(agentContext), default);
Check(agentContext.Order is { Found: true }, "структурированный tool call исполняет C#");
Check(script.LastInput!.Any(x => x["type"]?.GetValue<string>() == "function_call_output"),
    "результат инструмента возвращается модели");
Check(!agent.UsedFallback, "агент завершает ответ");

using var http = new HttpClient(new CaptureHandler(
    """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"OK"}]}]}"""))
    { BaseAddress = new Uri("https://fixture.invalid/v1/") };
var api = new OpenAiApi(http, new("fixture-not-a-key", "gpt-5.4-mini", "text-embedding-3-small"));
var response = await api.RespondAsync("test", [OpenAiApi.UserMessage("test")], SupportTools.Definitions(), default);
Check(OpenAiApi.OutputText(response) == "OK", "парсер Responses API");
Check(KnowledgeRepository.Split(new string('x', 1700)).Length == 2, "разбиение с перекрытием");
var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
try
{
    System.Globalization.CultureInfo.CurrentCulture = new("de-DE");
    Check(KnowledgeRepository.VectorText([0.1f, 0.2f]) == "[0.100000001,0.200000003]",
        "вектор использует точку, независимо от локали");
}
finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }

using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    var state = Context(Request());
    await new SupportWorkflow(new ReplyComposer(new FakeModel(() => "unused")))
        .RunAsync(state, Tools(state), cancelled.Token);
    throw new InvalidOperationException("Отмена проигнорирована.");
}
catch (OperationCanceledException) { checks++; }

Console.WriteLine($"OK: {checks} офлайн-проверок. Внешние API и PostgreSQL не вызывались.");

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
