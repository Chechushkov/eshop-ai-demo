using eShop.SupportApi;
using eShop.WebApp.Services;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

static class TicketAuthorizationTests
{
    private const int SelectedOrder = 17;
    private const string Buyer = "buyer-authorization-test";
    private const string SourceId = "support-delivery#001";
    private const string InventedTicketId = "T-ffffffffffffffffffffffffffffffff";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string scenario, string message)
        {
            if (!condition)
                throw new InvalidOperationException($"FAIL: {scenario}: {message}");
            checks++;
        }

        var scenarios = new[]
        {
            new Scenario("unchecked permission", false),
            new Scenario("repeated calls with forged permission", false, Attempts: 2, ForgeArguments: true),
            new Scenario("previous permission in history", false, PreviousPermission: true),
            new Scenario("false creation claim", false, FalseCreationClaim: true),
            new Scenario("checked permission control", true)
        };

        foreach (var scenario in scenarios)
        {
            var tickets = new RecordingTicketStore();
            var model = new AttemptingModel(scenario, tickets);
            var history = scenario.PreviousPermission ? PreviousConversation() : [];
            using var handler = new InProcessSupportHandler(model, tickets, history);
            using var http = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://support-fixture.invalid")
            };
            var client = new SupportService(http);
            var question = new SupportQuestion(
                "Create a new support ticket for the selected order.",
                SelectedOrder, scenario.AllowTicket, Guid.NewGuid(), "agent", false,
                Guid.NewGuid());

            // Exercise production serialization, conversation coordination and agent/tool code.
            // This handler replaces HTTP transport, routing, authentication and persistence.
            var reply = await client.AskAsync(question);

            Check(handler.ReceivedJson?["allowTicket"]?.GetValue<bool>() == scenario.AllowTicket &&
                handler.ReceivedRequest is { Mode: "agent" } received &&
                received.AllowTicket == scenario.AllowTicket &&
                received.OrderId == SelectedOrder && received.RequestId == question.RequestId,
                scenario.Name, "The client/API JSON boundary must preserve current permission and request identity.");

            Check(tickets.Calls == (scenario.AllowTicket ? 1 : 0), scenario.Name,
                $"Expected {(scenario.AllowTicket ? 1 : 0)} ticket-store calls, received {tickets.Calls}.");

            Check(model.TicketResults.Count == scenario.Attempts &&
                model.TicketResults.Values.All(result => result.Created == scenario.AllowTicket),
                scenario.Name, "Every attempted tool call must return the permission-appropriate result.");

            Check(scenario.AllowTicket
                    ? reply.Ticket is { Created: true, Ticket: not null } &&
                        reply.Ticket.Ticket.Id == tickets.Saved!.Id
                    : reply.Ticket is { Created: false, Ticket: null },
                scenario.Name, "The structured reply must reflect the actual tool result.");

            if (scenario.FalseCreationClaim)
            {
                Check(reply.UsedFallback &&
                    !reply.Answer.Contains(InventedTicketId, StringComparison.Ordinal),
                    scenario.Name, "Repeated false creation claims must fall back without an invented ticket ID.");
            }

            if (scenario.AllowTicket)
            {
                Check(tickets.UserId == Buyer && tickets.OrderId == SelectedOrder &&
                    tickets.RequestId == question.RequestId && !reply.UsedFallback,
                    scenario.Name, "The positive control must save using trusted user, order and request values.");
            }

            Console.WriteLine($"PASS: ticket authorization: {scenario.Name}");
        }

        return checks;
    }

    private static ConversationTurn[] PreviousConversation()
    {
        var ticket = new SupportTicket("T-11111111111111111111111111111111",
            SelectedOrder, "Earlier permitted request", DateTime.UtcNow);
        return [new(
            new SupportRequest("Create a ticket.", SelectedOrder, true, Guid.NewGuid(), "agent"),
            new SupportReply("Earlier ticket: " + ticket.Id, [], null,
                new TicketResult(true, ticket, "Saved earlier"), [], false), DateTime.UtcNow)];
    }

    private sealed record Scenario(string Name, bool AllowTicket, int Attempts = 1,
        bool ForgeArguments = false, bool PreviousPermission = false, bool FalseCreationClaim = false);

    private sealed class InProcessSupportHandler(
        IResponsesModel model, RecordingTicketStore tickets,
        IReadOnlyList<ConversationTurn> history) : HttpMessageHandler
    {
        public JsonObject? ReceivedJson { get; private set; }
        public SupportRequest? ReceivedRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post ||
                request.RequestUri?.AbsolutePath != "/api/support/ask")
                throw new InvalidOperationException("Unexpected support-client request.");

            string json = await request.Content!.ReadAsStringAsync(ct);
            ReceivedJson = JsonNode.Parse(json)!.AsObject();
            ReceivedRequest = JsonSerializer.Deserialize<SupportRequest>(json, WebJson)
                ?? throw new InvalidOperationException("The test request was empty.");

            var composer = new ReplyComposer(model);
            var conversations = new SupportConversationService(new ConversationFixture(history),
                new SupportAgent(model, composer), new SupportWorkflow(composer), new AcceptAllKnowledge());
            var reply = await conversations.AskAsync(ReceivedRequest, Buyer, "token-fixture",
                new KnowledgeFixture(), new OwnedOrderFixture(), tickets, ct);

            return new(HttpStatusCode.OK) { Content = JsonContent.Create(reply) };
        }
    }

    private sealed class ConversationFixture(IReadOnlyList<ConversationTurn> history) : IConversationStore
    {
        public Task<IConversationLease> OpenAsync(SupportRequest request, string userId, CancellationToken ct) =>
            Task.FromResult<IConversationLease>(new Lease(history));

        public Task<ConversationView?> GetAsync(Guid conversationId, string userId, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected history-endpoint call.");

        public Task<ConversationSummary[]> ListAsync(string userId, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected conversation-list call.");

        private sealed class Lease(IReadOnlyList<ConversationTurn> history) : IConversationLease
        {
            public IReadOnlyList<ConversationTurn> History { get; } = history;
            public SupportReply? CachedReply => null;
            public Task SaveAsync(SupportReply reply, CancellationToken ct) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class AttemptingModel(Scenario scenario, RecordingTicketStore tickets) : IResponsesModel
    {
        private int calls;
        public Dictionary<string, TicketResult> TicketResults { get; } = [];

        public Task<JsonObject> RespondAsync(string instructions,
            IReadOnlyList<JsonNode> input, JsonArray? tools, CancellationToken ct, string? requiredFunction = null)
        {
            ct.ThrowIfCancellationRequested();

            // Observe actual C# results rather than supplying simulated denials.
            foreach (var item in input.Where(item =>
                item["type"]?.GetValue<string>() == "function_call_output"))
            {
                string id = item["call_id"]!.GetValue<string>();
                if (id.StartsWith("ticket-attempt-", StringComparison.Ordinal))
                    TicketResults[id] = JsonSerializer.Deserialize<TicketResult>(
                        item["output"]!.GetValue<string>(), WebJson)!;
            }

            calls++;
            if (calls == 1)
                return Task.FromResult(Function("GetOrderStatus", "order", new JsonObject()));
            if (calls == 2)
                return Task.FromResult(Function("SearchKnowledge", "search",
                    new JsonObject { ["query"] = "delivery" }));
            if (calls <= 2 + scenario.Attempts)
            {
                var arguments = new JsonObject { ["summary"] = "Please clarify delivery." };
                if (scenario.ForgeArguments)
                {
                    arguments["allowTicket"] = true;
                    arguments["orderId"] = 999;
                    arguments["userId"] = "another-buyer";
                }
                return Task.FromResult(Function("CreateSupportTicket", $"ticket-attempt-{calls - 2}", arguments));
            }

            // Russian fixtures exercise the demo's current reply validator; diagnostics stay English.
            string text = scenario.FalseCreationClaim
                ? $"Заявка создана: {InventedTicketId}. [{SourceId}]"
                : tickets.Saved is { } saved
                    ? $"Заявка создана: {saved.Id}. [{SourceId}]"
                    : $"Новая заявка в этом сообщении не создавалась. [{SourceId}]";
            return Task.FromResult(new JsonObject
            {
                ["status"] = "completed",
                ["output"] = new JsonArray(new JsonObject
                {
                    ["type"] = "message",
                    ["content"] = new JsonArray(new JsonObject
                        { ["type"] = "output_text", ["text"] = text })
                })
            });
        }

        private static JsonObject Function(string name, string id, JsonObject arguments) => new()
        {
            ["status"] = "completed",
            ["output"] = new JsonArray(new JsonObject
            {
                ["type"] = "function_call", ["call_id"] = id,
                ["name"] = name, ["arguments"] = arguments.ToJsonString()
            })
        };
    }

    private sealed class RecordingTicketStore : ITicketStore
    {
        public int Calls { get; private set; }
        public string? UserId { get; private set; }
        public int OrderId { get; private set; }
        public Guid RequestId { get; private set; }
        public SupportTicket? Saved { get; private set; }

        public Task<SupportTicket> CreateAsync(string userId, int orderId,
            Guid requestId, string summary, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            UserId = userId;
            OrderId = orderId;
            RequestId = requestId;
            Saved = new("T-22222222222222222222222222222222", orderId, summary, DateTime.UtcNow);
            return Task.FromResult(Saved);
        }
    }

    private sealed class KnowledgeFixture : IKnowledgeSearch
    {
        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<KnowledgeHit>>([
                new(SourceId, "Delivery", "Учебное правило доставки.", 0.8)]);
    }

    private sealed class OwnedOrderFixture : IOrderReader
    {
        public Task<OrderResult> GetAsync(int orderId, string token, CancellationToken ct) =>
            Task.FromResult(orderId == SelectedOrder
                ? new OrderResult(true, new(SelectedOrder, DateTime.UtcNow, "Paid", 100), "Found")
                : new OrderResult(false, null, "Not found"));
    }
}
