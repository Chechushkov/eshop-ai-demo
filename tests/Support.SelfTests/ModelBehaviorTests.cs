using eShop.SupportApi;
using eShop.WebApp.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

static class ModelBehaviorTests
{
    // Keep demo-language fixtures separate from English diagnostics.
    private const string ValidReply = "Правило [support-delivery#001].";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: model behavior: " + message);
            checks++;
        }

        RunContext State(string mode = "agent", bool retry = false,
            IReadOnlyList<ConversationTurn>? history = null) =>
            new(new SupportRequest("Проверь заказ и правила доставки.", 17, false,
                    Guid.NewGuid(), mode, retry, Guid.NewGuid()),
                "buyer-behavior-test", "token-fixture", history);

        SupportTools Tools(RunContext state, IKnowledgeSearch? knowledge = null) =>
            new(knowledge ?? new FakeKnowledge(), new FakeOrders(), new FakeTickets(), state);

        async Task<SupportReply> Agent(RunContext state, JsonObject[] responses,
            IKnowledgeSearch? knowledge = null)
        {
            var model = new ScriptModel(responses);
            return await new SupportAgent(model, new ReplyComposer(model))
                .RunAsync(state, Tools(state, knowledge), default);
        }

        var normal = await Agent(State(), [OrderCall(), SearchCall(), Text()]);
        Check(HasBehavior(normal, [], false) && !normal.UsedFallback,
            "Model-selected reads need no automatic completion or recomposition.");

        var omittedState = State();
        var omitted = await Agent(omittedState, [Text(), Text()]);
        Check(HasBehavior(omitted, ["SearchKnowledge", "GetOrderStatus"], true) && !omitted.UsedFallback,
            "Missing reads are recorded in execution order and trigger recomposition.");

        var missingOrder = await Agent(State(), [SearchCall(), Text(), Text()]);
        Check(HasBehavior(missingOrder, ["GetOrderStatus"], true),
            "Only the automatically completed order read is recorded.");

        var missingSearch = await Agent(State(), [OrderCall(), Text(), Text()]);
        Check(HasBehavior(missingSearch, ["SearchKnowledge"], true),
            "Only the automatically completed knowledge search is recorded.");

        var emptyKnowledge = new EmptyKnowledge();
        var noHits = await Agent(State(), [OrderCall(), SearchCall(), FakeModel.Text("Нет подходящих правил."),
            FakeModel.Text("Нет подходящих правил.")], emptyKnowledge);
        Check(emptyKnowledge.Calls == 2 && HasBehavior(noHits, ["SearchKnowledge"], true) && noHits.Sources.Count == 0,
            "Completion records application intervention even when the model searched and both searches found no hits.");

        var ticketState = State();
        ticketState = new(ticketState.Request with { AllowTicket = true }, ticketState.UserId, ticketState.AccessToken);
        var ticketScript = new ScriptModel([
            OrderCall(), SearchCall(), Call("CreateSupportTicket", """{"summary":"Уточнить доставку"}""")
        ]);
        // Produce the final text from the actual tool result rather than a guessed ticket ID.
        var ticketResponse = new TicketReplyModel(ticketScript, ticketState);
        var ticketReply = await new SupportAgent(ticketResponse, new ReplyComposer(ticketResponse))
            .RunAsync(ticketState, Tools(ticketState), default);
        Check(ticketReply.Ticket is { Created: true } && HasBehavior(ticketReply, [], false),
            "Ownership rechecking inside a selected ticket tool is not automatic completion.");

        var repaired = await Agent(State(), [OrderCall(), SearchCall(), FakeModel.Text("Нет ссылки."), Text()]);
        Check(HasBehavior(repaired, [], true) && !repaired.UsedFallback,
            "Validation repair is recorded even when all reads were selected by the model.");

        var fallback = await Agent(State(), [OrderCall(), SearchCall(),
            FakeModel.Text("Нет ссылки."), FakeModel.Text("Нет ссылки.")]);
        Check(HasBehavior(fallback, [], true) && fallback.UsedFallback,
            "Recomposition and deterministic fallback remain separate observations.");

        var exhausted = await Agent(State(), [SearchCall(), OrderCall(), OrderCall(), OrderCall(),
            OrderCall(), OrderCall(), Text()]);
        Check(HasBehavior(exhausted, [], true) && !exhausted.UsedFallback,
            "The extra composition after an exhausted selection loop is recorded.");

        foreach (bool retry in new[] { false, true })
        {
            var state = State("workflow", retry);
            var model = new FakeModel(() => ValidReply);
            var reply = await new SupportWorkflow(new ReplyComposer(model)).RunAsync(state, Tools(state), default);
            Check(HasBehavior(reply, [], retry) && !reply.UsedFallback,
                $"Normal workflow reads are not completion; recomposition matches retry={retry}.");
        }

        var badWorkflow = State("workflow");
        var badModel = new FakeModel(() => "Нет ссылки.");
        var workflowFallback = await new SupportWorkflow(new ReplyComposer(badModel))
            .RunAsync(badWorkflow, Tools(badWorkflow), default);
        Check(HasBehavior(workflowFallback, [], true) && workflowFallback.UsedFallback,
            "Workflow validation retry is recorded before fallback.");

        var previous = new ConversationTurn(State().Request, normal, DateTime.UtcNow);
        var followUp = State("workflow", history: [previous]);
        var followUpModel = new FakeModel(() => ValidReply);
        var followUpReply = await new SupportWorkflow(new ReplyComposer(followUpModel))
            .RunAsync(followUp, Tools(followUp), default);
        Check(followUpModel.Calls == 2 && HasBehavior(followUpReply, [], false),
            "A retrieval-query rewrite is not reply recomposition.");

        // Use the same JSON options as API responses and conversation persistence.
        string json = JsonSerializer.Serialize(omitted, WebJson);
        var restored = JsonSerializer.Deserialize<SupportReply>(json, WebJson)!;
        var client = JsonSerializer.Deserialize<SupportAnswer>(json, WebJson)!;
        Check(HasBehavior(restored, ["SearchKnowledge", "GetOrderStatus"], true),
            "Behavior survives the server reply JSON round trip.");
        Check(client.Behavior is { ReplyRecomposed: true } info &&
            info.AutoCompletedActions.SequenceEqual(new[] { "SearchKnowledge", "GetOrderStatus" }),
            "The production WebApp reply contract reads the structured fields.");

        var legacyJson = JsonNode.Parse(json)!.AsObject();
        legacyJson.Remove("behavior");
        var legacy = JsonSerializer.Deserialize<SupportReply>(legacyJson.ToJsonString(), WebJson)!;
        var legacyClient = JsonSerializer.Deserialize<SupportAnswer>(legacyJson.ToJsonString(), WebJson)!;
        Check(legacy.Behavior is null && legacyClient.Behavior is null,
            "Missing legacy observations remain unknown on both contracts.");

        omittedState.AutoCompletedActions.Clear();
        omittedState.ReplyRecomposed = false;
        Check(HasBehavior(omitted, ["SearchKnowledge", "GetOrderStatus"], true),
            "A finished reply holds a snapshot independent of its mutable run context.");

        foreach (var cached in new[] { restored, legacy })
        {
            var dependencies = new UnexpectedDependencies();
            var composer = new ReplyComposer(dependencies);
            var service = new SupportConversationService(new CachedStore(cached),
                new SupportAgent(dependencies, composer), new SupportWorkflow(composer),
                new SupportMetricsCollector(new("fixture-not-a-key", "fixture-model", "fixture-embedding")));
            var replay = await service.AskAsync(State().Request, "buyer-behavior-test", "token-fixture",
                dependencies, dependencies, dependencies, default);
            Check(cached.Behavior is null
                    ? replay.Behavior is null
                    : HasBehavior(replay, ["SearchKnowledge", "GetOrderStatus"], true),
                "Replay preserves original observations, including unknown legacy data.");
            Check(replay.Metrics is { IsReplay: true, ModelCalls: 0, EmbeddingCalls: 0 },
                "Original generation behavior is retained while replay reports zero model calls.");
        }

        Console.WriteLine("PASS: structured model behavior and replay compatibility");
        return checks;
    }

    private static bool HasBehavior(SupportReply reply, string[] actions, bool recomposed) =>
        reply.Behavior is { } value && value.AutoCompletedActions.SequenceEqual(actions) &&
        value.ReplyRecomposed == recomposed;

    private static JsonObject Text() => FakeModel.Text(ValidReply);
    private static JsonObject OrderCall() => Call("GetOrderStatus", "{}");
    private static JsonObject SearchCall() => Call("SearchKnowledge", """{"query":"delivery"}""");
    private static JsonObject Call(string name, string arguments)
    {
        var response = FakeModel.Function(name, arguments);
        response["output"]![0]!["call_id"] = "fixture-" + Guid.NewGuid().ToString("N");
        return response;
    }

    private sealed class EmptyKnowledge : IKnowledgeSearch
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<KnowledgeHit>>([]);
        }
    }

    private sealed class TicketReplyModel(ScriptModel script, RunContext state) : IResponsesModel
    {
        public Task<JsonObject> RespondAsync(string instructions, IReadOnlyList<JsonNode> input,
            JsonArray? tools, CancellationToken ct) => state.Ticket is { Created: true, Ticket: not null }
            ? Task.FromResult(FakeModel.Text(ValidReply + " Заявка: " + state.Ticket.Ticket.Id))
            : script.RespondAsync(instructions, input, tools, ct);
    }

    private sealed class CachedStore(SupportReply cached) : IConversationStore
    {
        public Task<IConversationLease> OpenAsync(SupportRequest request, string userId, CancellationToken ct) =>
            Task.FromResult<IConversationLease>(new Lease(cached));
        public Task<ConversationView?> GetAsync(Guid id, string userId, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected history read during replay.");
        public Task<ConversationSummary[]> ListAsync(string userId, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected conversation list during replay.");
        private sealed class Lease(SupportReply cached) : IConversationLease
        {
            public IReadOnlyList<ConversationTurn> History => [];
            public SupportReply? CachedReply => cached;
            public Task SaveAsync(SupportReply reply, CancellationToken ct) =>
                throw new InvalidOperationException("Replay must not save a new reply.");
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class UnexpectedDependencies : IResponsesModel, IKnowledgeSearch, IOrderReader, ITicketStore
    {
        public Task<JsonObject> RespondAsync(string instructions, IReadOnlyList<JsonNode> input,
            JsonArray? tools, CancellationToken ct) =>
            throw new InvalidOperationException("Replay must not call the model.");
        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, CancellationToken ct) =>
            throw new InvalidOperationException("Replay must not search knowledge.");
        public Task<OrderResult> GetAsync(int orderId, string token, CancellationToken ct) =>
            throw new InvalidOperationException("Replay must not read an order.");
        public Task<SupportTicket> CreateAsync(string userId, int orderId, Guid requestId,
            string summary, CancellationToken ct) =>
            throw new InvalidOperationException("Replay must not write a ticket.");
    }
}
