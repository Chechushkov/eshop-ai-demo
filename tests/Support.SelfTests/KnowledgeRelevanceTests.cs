using eShop.SupportApi;
using System.Net;
using System.Text.Json.Nodes;

static class KnowledgeRelevanceTests
{
    private const string Question =
        "Сколько стоит подарочная упаковка и можно ли заказать гравировку на кофемолке в вашем магазине?";
    private const string UnrelatedId = "support-returns-opened#001";
    private static readonly KnowledgeHit Unrelated = new(UnrelatedId, "Opened box",
        "An opened box alone does not prevent returning an unused item.", 0.99);
    private const string UnsupportedDraft =
        "Подарочная упаковка стоит 500 рублей, гравировка доступна [support-returns-opened#001].";

    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: knowledge relevance: " + message);
            checks++;
        }
        RunContext State(string mode = "workflow", int? order = null, bool allow = false, string question = Question) =>
            new(new SupportRequest(question, order, allow, Guid.NewGuid(), mode),
                "private-buyer-fixture", "private-access-token-fixture");

        foreach (string mode in new[] { "workflow", "agent" })
        {
            var state = State(mode);
            var search = new FixtureKnowledge([Unrelated]);
            var selectorModel = new RelevanceModel(() => Selection());
            var selector = new KnowledgeRelevance(selectorModel);
            var draft = new DraftModel(mode);
            var tickets = new FakeTickets();
            var tools = new SupportTools(search, new FakeOrders(), tickets, state, selector);
            var composer = new ReplyComposer(draft);
            var reply = mode == "workflow"
                ? await new SupportWorkflow(composer).RunAsync(state, tools, default)
                : await new SupportAgent(draft, composer).RunAsync(state, tools, default);

            // A real selector receives an unrelated high-score candidate and selects none.
            Check(reply.Sources.Count == 0 && !reply.Answer.Contains('[') &&
                !reply.Answer.Contains("500", StringComparison.Ordinal),
                $"{mode}: a valid but irrelevant citation and an invented price cannot reach the final reply.");
            Check(reply.Answer.Contains(ReplyComposer.InsufficientKnowledgeMessage, StringComparison.Ordinal)
                && !reply.UsedFallback,
                $"{mode}: missing evidence produces the normal confirmed-data reply, not fallback.");
            Check(search.Calls == 1 && selectorModel.Calls == 1 && state.KnowledgeSearchCompleted,
                $"{mode}: an empty relevant-source set does not repeat retrieval or selection.");
            Check(draft.Calls == (mode == "workflow" ? 0 : 2),
                $"{mode}: generating a missing-information reply adds no model composition call.");
            Check(tickets.Writes == 0 && reply.Ticket is null &&
                ReplyComposer.Validate(reply.Answer, state).Length == 0,
                $"{mode}: the reply passes validation without a citation and creates no ticket.");
            Check(reply.Behavior is { } behavior && behavior.AutoCompletedActions.Length == 0 &&
                behavior.ReplyRecomposed == (mode == "agent"),
                $"{mode}: deterministic replacement and automatically completed reads remain distinct.");
            string payload = string.Join("\n", selectorModel.LastInput!.Select(item => item.ToJsonString()));
            Check(payload.Contains(Unrelated.Text, StringComparison.Ordinal) &&
                !payload.Contains(state.AccessToken, StringComparison.Ordinal) &&
                !payload.Contains(state.UserId, StringComparison.Ordinal),
                $"{mode}: relevance sees candidate text without the buyer identity or access token.");
        }

        var relevant = new KnowledgeHit("support-warranty#001", "Warranty", "Contact support for a defect review.", 0.50);
        var subsetModel = new RelevanceModel(() => Selection(relevant.Id));
        var subset = await new KnowledgeRelevance(subsetModel).SelectAsync(
            "How do I report a defect?", "defect procedure", [Unrelated, relevant], [], default);
        Check(subset.Count == 1 && ReferenceEquals(subset[0], relevant),
            "The selector returns only original selected chunks without rewriting text or score.");

        var compound = State(question: "Можно вернуть неиспользованный товар? Что делать при неисправности?");
        var compoundSearch = new FixtureKnowledge([Unrelated, relevant]);
        var compoundModel = new RelevanceModel(() => Selection(UnrelatedId, relevant.Id));
        var compoundHits = await new SupportTools(compoundSearch, new FakeOrders(), new FakeTickets(), compound,
            new KnowledgeRelevance(compoundModel)).SearchKnowledgeAsync(compound.Request.Question, default);
        Check(compoundSearch.Calls == 3 && compoundModel.Calls == 1,
            "Compound retrieval selects relevance once after merging all query results.");
        Check(compoundHits.Count == 2 && compoundHits.Any(hit => hit.Id == relevant.Id),
            "Relevant evidence for both topics survives deduplication and selection.");

        var previousRequest = new SupportRequest("Можно вернуть товар?", null, false, Guid.NewGuid());
        var previousReply = new SupportReply("Обсуждался возврат товара.", [], null, null, [], false);
        var history = new[] { new ConversationTurn(previousRequest, previousReply, DateTime.UtcNow) };
        var historyModel = new RelevanceModel(() => Selection(relevant.Id));
        await new KnowledgeRelevance(historyModel).SelectAsync("А как обратиться?", "обращение в поддержку",
            [relevant], history, default);
        Check(historyModel.LastInput is { Count: 3 } messages &&
            messages[0]["content"]!.GetValue<string>() == previousRequest.Question &&
            JsonNode.Parse(messages[^1]["content"]!.GetValue<string>())!["Question"]!.GetValue<string>() == "А как обратиться?",
            "Relevance selection receives bounded conversation history and the current question separately.");

        var acceptedState = State();
        var acceptedTools = new SupportTools(new FixtureKnowledge([relevant]), new FakeOrders(),
            new FakeTickets(), acceptedState, new KnowledgeRelevance(subsetModel));
        await acceptedTools.SearchKnowledgeAsync("defect procedure", default);
        Check(ReplyComposer.Validate("Reply without a citation.", acceptedState).Length > 0,
            "A supported policy answer still requires a citation.");

        var absent = State();
        var noModel = new RelevanceModel(() => throw new InvalidOperationException("Unexpected selector call."));
        await new SupportTools(new FixtureKnowledge([]), new FakeOrders(), new FakeTickets(), absent,
            new KnowledgeRelevance(noModel)).SearchKnowledgeAsync(Question, default);
        Check(noModel.Calls == 0 && absent.HasNoRelevantKnowledge,
            "An empty candidate list completes search without calling the relevance model.");
        Check(ReplyComposer.Validate("Гравировка недоступна.", absent).Length > 0,
            "Missing evidence does not permit an unsupported service-availability claim without citations.");

        var selectedOrder = State(order: 17, allow: true,
            question: "Создай заявку по выбранному заказу, чтобы уточнить условия подарочной упаковки.");
        var ticketStore = new FakeTickets();
        var ticketModel = new FakeModel(() => throw new InvalidOperationException("Unexpected text generation."),
            needsTicket: true);
        var ticketTools = new SupportTools(new FixtureKnowledge([Unrelated]), new FakeOrders(), ticketStore,
            selectedOrder, new KnowledgeRelevance(new RelevanceModel(() => Selection())));
        var ticketReply = await new SupportWorkflow(new ReplyComposer(ticketModel))
            .RunAsync(selectedOrder, ticketTools, default);
        Check(ticketStore.Writes == 1 && ticketReply.Ticket is { Created: true, Ticket: not null } saved &&
            ticketReply.Answer.Contains(saved.Ticket.Id, StringComparison.Ordinal) &&
            ticketReply.Answer.Contains("Заказ 17: Paid.", StringComparison.Ordinal),
            "A missing-knowledge reply preserves an authorized ticket ID and confirmed current order status.");
        Check(!ticketReply.UsedFallback && ticketModel.Calls == 1,
            "The ordinary ticket decision remains separate from deterministic reply composition.");

        var noTicketState = State(order: 17, allow: true);
        var noTicketStore = new FakeTickets();
        var noTicketModel = new ScriptModel([FakeModel.Function(TicketPolicy.DecisionFunction,
            """{"needsTicket":false,"reason":"insufficient_context"}""")]);
        var noTicketReply = await new SupportWorkflow(new ReplyComposer(noTicketModel)).RunAsync(noTicketState,
            new SupportTools(new FixtureKnowledge([Unrelated]), new FakeOrders(), noTicketStore, noTicketState,
                new KnowledgeRelevance(new RelevanceModel(() => Selection()))), default);
        Check(noTicketStore.Writes == 0 && noTicketReply.Ticket is null &&
            noTicketReply.Answer.Contains(TicketPolicy.NoTicketCreated, StringComparison.Ordinal),
            "Missing information plus permission alone does not create a ticket.");

        var malformed = new List<JsonObject>
        {
            Selection("invented#001"), Selection(UnrelatedId, UnrelatedId),
            FakeModel.Function(KnowledgeRelevance.FunctionName, "not json"),
            FakeModel.Function(KnowledgeRelevance.FunctionName, "[]"),
            FakeModel.Function(KnowledgeRelevance.FunctionName, "{}"),
            FakeModel.Function(KnowledgeRelevance.FunctionName, """{"relevantSourceIds":null}"""),
            FakeModel.Function(KnowledgeRelevance.FunctionName, """{"relevantSourceIds":[123]}"""),
            FakeModel.Function(KnowledgeRelevance.FunctionName, """{"relevantSourceIds":[],"allowTicket":true}"""),
            FakeModel.Function(KnowledgeRelevance.FunctionName, """{"relevantSourceIds":[],"relevantSourceIds":[]}"""),
            FakeModel.Function("CreateSupportTicket", "{}"), FakeModel.Text("No decision"),
            new() { ["status"] = "completed" }
        };
        var duplicate = Selection();
        duplicate["output"]!.AsArray().Add(duplicate["output"]![0]!.DeepClone());
        malformed.Add(duplicate);
        var incomplete = Selection();
        incomplete["status"] = "incomplete";
        malformed.Add(incomplete);

        foreach (var response in malformed)
        {
            var state = State();
            try
            {
                await new SupportTools(new FixtureKnowledge([Unrelated]), new FakeOrders(), new FakeTickets(), state,
                    new KnowledgeRelevance(new RelevanceModel(() => response))).SearchKnowledgeAsync(Question, default);
                throw new InvalidOperationException("An invalid selector decision was accepted.");
            }
            catch (UpstreamException)
            {
                Check(state.Sources.Count == 0 && !state.KnowledgeSearchCompleted && !state.Request.AllowTicket,
                    "Malformed or invented selection is rejected without publishing evidence or changing permission.");
            }
        }

        using (var cancelled = new CancellationTokenSource())
        {
            var state = State();
            var cancellationModel = new RelevanceModel(() =>
            {
                cancelled.Cancel();
                return Selection(UnrelatedId);
            });
            try
            {
                await new SupportTools(new FixtureKnowledge([Unrelated]), new FakeOrders(), new FakeTickets(), state,
                    new KnowledgeRelevance(cancellationModel)).SearchKnowledgeAsync(Question, cancelled.Token);
                throw new InvalidOperationException("Cancellation was ignored.");
            }
            catch (OperationCanceledException)
            {
                Check(state.Sources.Count == 0 && !state.KnowledgeSearchCompleted,
                    "Cancellation during relevance selection publishes no partial result.");
            }
        }

        // Use the production HTTP client and collector with an in-memory handler.
        foreach (string mode in new[] { "workflow", "agent" })
        {
            var settings = new OpenAiSettings("fixture-not-a-key", "fixture-model", "fixture-embedding");
            var meter = new SupportMetricsCollector(settings);
            using var handler = new MeteredHandler();
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/v1/") };
            var api = new OpenAiApi(http, settings, meter);
            var state = State(mode);
            var tools = new SupportTools(new FixtureKnowledge([Unrelated]), new FakeOrders(), new FakeTickets(),
                state, new KnowledgeRelevance(api));
            meter.Start();
            if (mode == "workflow")
                await new SupportWorkflow(new ReplyComposer(api)).RunAsync(state, tools, default);
            else
                await new SupportAgent(api, new ReplyComposer(api)).RunAsync(state, tools, default);
            var metrics = meter.Finish();
            int expectedCalls = mode == "workflow" ? 1 : 3;
            Check(metrics.ModelCalls == expectedCalls && metrics.InputTokens == expectedCalls * 10 &&
                metrics.OutputTokens == expectedCalls * 2 && handler.Calls == expectedCalls,
                $"{mode}: relevance calls are included in production model-call and token metrics.");
        }

        Console.WriteLine("PASS: knowledge relevance, grounded abstention and selector metrics");
        return checks;
    }

    private static JsonObject Selection(params string[] ids) => FakeModel.Function(KnowledgeRelevance.FunctionName,
        new JsonObject { ["relevantSourceIds"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) }
            .ToJsonString());

    private sealed class FixtureKnowledge(IReadOnlyList<KnowledgeHit> hits) : IKnowledgeSearch
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(hits);
        }
    }

    private sealed class RelevanceModel(Func<JsonObject> respond) : IResponsesModel
    {
        public int Calls { get; private set; }
        public IReadOnlyList<JsonNode>? LastInput { get; private set; }
        public Task<JsonObject> RespondAsync(string instructions, IReadOnlyList<JsonNode> input,
            JsonArray? tools, CancellationToken ct, string? requiredFunction = null)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            LastInput = input;
            return Task.FromResult(respond());
        }
    }

    private sealed class DraftModel(string mode) : IResponsesModel
    {
        public int Calls { get; private set; }
        public Task<JsonObject> RespondAsync(string instructions, IReadOnlyList<JsonNode> input,
            JsonArray? tools, CancellationToken ct, string? requiredFunction = null)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(mode == "agent" && Calls == 1
                ? FakeModel.Function("SearchKnowledge", new JsonObject { ["query"] = Question }.ToJsonString())
                : FakeModel.Text(UnsupportedDraft));
        }
    }

    private sealed class MeteredHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        private int draftCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var tools = body["tools"]!.AsArray();
            JsonObject response = tools.Count == 1 && tools[0]?["name"]?.GetValue<string>() == KnowledgeRelevance.FunctionName
                ? Selection()
                : ++draftCalls == 1
                    ? FakeModel.Function("SearchKnowledge", new JsonObject { ["query"] = Question }.ToJsonString())
                    : FakeModel.Text(UnsupportedDraft);
            response["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 2 };
            return new(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
