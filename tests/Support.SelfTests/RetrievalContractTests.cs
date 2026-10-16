using eShop.SupportApi;
using System.Net;
using System.Text.Json.Nodes;

static class RetrievalContractTests
{
    private const string ReturnQuestion = "Can I return an unused grinder with an opened box?";
    private const string DefectQuestion = "How do I report a defect?";
    private const string Question = ReturnQuestion + " " + DefectQuestion + " I only want the general rules.";
    private const string ResolvedQuery = "возврат неиспользованного товара";
    private const string OpenedId = "support-returns-opened#001";
    private const string WarrantyId = "support-warranty#007";
    private const string HistoryOnlyId = "support-history-only#001";
    private const string Draft = "Открытая коробка не мешает возврату неиспользованного товара " +
        "при сохранении товарного вида и комплекта [support-returns-opened#001]. " +
        "При неисправности обратитесь в поддержку с описанием проблемы [support-warranty#007].";

    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: retrieval contracts: " + message);
            checks++;
        }

        foreach (string mode in new[] { "workflow", "agent" })
        {
            var previous = new SupportReply($"Previous policy [{HistoryOnlyId}].", [], null, null, [], false);
            var history = Enumerable.Range(0, 21).Select(_ => new ConversationTurn(
                new SupportRequest("Earlier question", null, false, Guid.NewGuid()), previous, DateTime.UtcNow)).ToArray();
            var state = new RunContext(new(Question, null, false, Guid.NewGuid(), mode),
                "private-contract-buyer", "private-contract-token", history);
            var knowledge = new RecordingKnowledge();
            var tickets = new FakeTickets();
            bool requestedSearch = false;
            using var handler = new CaptureHandler(body => FlowResponse(body));
            using var http = Client(handler);
            var metrics = new SupportMetricsCollector(Settings());
            var api = new OpenAiApi(http, Settings(), metrics);
            var composer = new ReplyComposer(api);
            var tools = new SupportTools(knowledge, new FakeOrders(), tickets, state, new KnowledgeRelevance(api));
            metrics.Start();
            var reply = mode == "workflow"
                ? await new SupportWorkflow(composer).RunAsync(state, tools, default)
                : await new SupportAgent(api, composer).RunAsync(state, tools, default);
            var measurement = metrics.Finish();

            Check(knowledge.Queries.SequenceEqual(new[] { ResolvedQuery, ReturnQuestion, DefectQuestion }),
                $"{mode}: a history rewrite or short tool argument retains both original subquestions.");
            Check(reply.Sources.Any(hit => hit.Id == WarrantyId) && reply.Sources.Any(hit => hit.Id == OpenedId) &&
                reply.Answer.Contains($"[{WarrantyId}]", StringComparison.Ordinal) &&
                reply.Answer.Contains(ReplyNotices.WarrantyAuthority, StringComparison.Ordinal),
                $"{mode}: evidence and the mandatory warranty notice survive a long-conversation search.");

            var selection = handler.Bodies.Single(IsSelection);
            var choice = selection["tool_choice"];
            Check(choice?["type"]?.GetValue<string>() == "function" &&
                choice["name"]?.GetValue<string>() == KnowledgeRelevance.FunctionName &&
                selection["parallel_tool_calls"]?.GetValue<bool>() == false,
                $"{mode}: the production HTTP request requires exactly the selector function.");

            var enumNode = selection["tools"]![0]!["parameters"]!["properties"]!["relevantSourceIds"]!["items"]!["enum"];
            Check(enumNode is JsonArray,
                $"{mode}: the selector schema restricts source IDs with an enum.");
            var schemaIds = enumNode!.AsArray().Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            var evidence = JsonNode.Parse(selection["input"]!.AsArray()[^1]!["content"]!.GetValue<string>())!;
            var candidateIds = evidence["Candidates"]!.AsArray().Select(hit => hit!["Id"]!.GetValue<string>());
            Check(schemaIds.SetEquals(candidateIds) && schemaIds.Contains(WarrantyId) && !schemaIds.Contains(HistoryOnlyId),
                $"{mode}: the strict schema admits current candidate IDs, never citations copied from history.");
            Check(evidence["Question"]!.GetValue<string>() == Question &&
                selection["input"]!.AsArray().Count == 2 * ConversationMemory.ContextTurns + 1 &&
                !selection.ToJsonString().Contains(state.AccessToken, StringComparison.Ordinal) &&
                !selection.ToJsonString().Contains(state.UserId, StringComparison.Ordinal),
                $"{mode}: selection retains the original question without authentication data.");
            Check(measurement.ModelCalls == 3 && measurement.InputTokens == 30 && measurement.OutputTokens == 6 &&
                handler.Bodies.Count == 3 && !reply.UsedFallback && tickets.Writes == 0,
                $"{mode}: the bounded search adds no model planning calls or ticket writes and records usage.");
            Check(ReplyComposer.Validate(reply.Answer, state).Length == 0 &&
                reply.Behavior is { ReplyRecomposed: false, AutoCompletedActions.Length: 0 },
                $"{mode}: the real workflow/agent completes normally after the HTTP contract is enforced.");

            JsonObject FlowResponse(JsonObject body)
            {
                if (IsSelection(body))
                {
                    // Under auto, answering without a function call is legal.
                    // Reproduce that failure unless the actual client forces the selector.
                    if (body["tool_choice"] is not JsonObject required ||
                        required["name"]?.GetValue<string>() != KnowledgeRelevance.FunctionName)
                        return FakeModel.Text("An answer instead of a selection call.");

                    var current = JsonNode.Parse(body["input"]!.AsArray()[^1]!["content"]!.GetValue<string>())!;
                    return Selection(current["Candidates"]!.AsArray()
                        .Select(hit => hit!["Id"]!.GetValue<string>())
                        .Where(id => id == OpenedId || id == WarrantyId).ToArray());
                }

                if (body["tools"] is JsonArray)
                {
                    if (requestedSearch)
                        return FakeModel.Text(Draft);
                    requestedSearch = true;
                    return FakeModel.Function("SearchKnowledge", new JsonObject { ["query"] = ResolvedQuery }.ToJsonString());
                }

                string currentMessage = body["input"]!.AsArray()[^1]!["content"]!.GetValue<string>();
                return currentMessage == Question
                    ? FakeModel.Text(new JsonObject { ["query"] = ResolvedQuery }.ToJsonString())
                    : FakeModel.Text(Draft);
            }
        }

        // Required calls are explicit. A single optional tool must still permit a
        // normal answer; the generic HTTP client does not infer intent by tool count.
        using var capture = new CaptureHandler(_ => FakeModel.Text("OK"));
        using var client = Client(capture);
        var transport = new OpenAiApi(client, Settings());
        await transport.RespondAsync("Optional lookup", [], TicketPolicy.DecisionTools(), default);
        Check(capture.Bodies[^1]["tool_choice"]?.GetValue<string>() == "auto",
            "An optional tool retains automatic choice even when it is the only tool.");
        await transport.RespondAsync("Decide", [], TicketPolicy.DecisionTools(), default,
            requiredFunction: TicketPolicy.DecisionFunction);
        Check(capture.Bodies[^1]["tool_choice"]?["name"]?.GetValue<string>() == TicketPolicy.DecisionFunction,
            "The workflow ticket decision explicitly requires its decision function.");
        await transport.RespondAsync("Compose", [], null, default);
        Check(!capture.Bodies[^1].ContainsKey("tool_choice") && !capture.Bodies[^1].ContainsKey("tools"),
            "Text composition does not receive a forced tool choice.");

        var duplicate = TicketPolicy.DecisionTools();
        duplicate.Add(duplicate[0]!.DeepClone());
        foreach (var (definitions, name) in new (JsonArray?, string)[]
            { (null, "Missing"), ([], "Missing"), (TicketPolicy.DecisionTools(), "Missing"),
              (TicketPolicy.DecisionTools(), ""), (duplicate, TicketPolicy.DecisionFunction) })
        {
            int before = capture.Bodies.Count;
            try
            {
                await transport.RespondAsync("Invalid request", [], definitions, default, requiredFunction: name);
                throw new InvalidOperationException("An undefined required function was accepted.");
            }
            catch (ArgumentException)
            {
                Check(capture.Bodies.Count == before,
                    "An undefined, empty or ambiguous required function fails before any HTTP request.");
            }
        }

        // HTTP 200 does not establish that a model decision meets the application
        // contract. Invalid responses remain failures, with safe diagnostic codes.
        foreach (var (response, reason) in new[]
        {
            (FakeModel.Text("Private model text"), "function_count"),
            (Selection("private-unavailable-source"), "unknown_source_id"),
            (Selection(OpenedId, OpenedId), "duplicate_source_id")
        })
        {
            using var invalidHandler = new CaptureHandler(_ => response.DeepClone().AsObject());
            using var invalidClient = Client(invalidHandler);
            var selector = new KnowledgeRelevance(new OpenAiApi(invalidClient, Settings()));
            try
            {
                await selector.SelectAsync("Return policy", "Return", [Opened()], [], default);
                throw new InvalidOperationException("Invalid evidence was accepted after HTTP 200.");
            }
            catch (UpstreamException error)
            {
                Check(error.Message.Contains("(" + reason + ")", StringComparison.Ordinal) &&
                    !error.Message.Contains("private-unavailable-source", StringComparison.Ordinal) &&
                    !error.Message.Contains("Private model text", StringComparison.Ordinal),
                    "Invalid decisions expose only a static reason code, never model text or source values.");
            }
        }

        Check(KnowledgeQueryPlanner.Build(ResolvedQuery, "One? Two? Three? Four?").Count == 3,
            "Preserving original subquestions still respects the three-query limit.");
        Check(KnowledgeQueryPlanner.Build(ResolvedQuery, "А как вернуть товар?").SequenceEqual(new[] { ResolvedQuery }),
            "A single follow-up question retains the resolved query without another search.");

        Console.WriteLine("PASS: history-aware retrieval and required decision calls");
        return checks;
    }

    private static OpenAiSettings Settings() => new("fixture-not-a-key", "fixture-model", "fixture-embedding");
    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
        { BaseAddress = new Uri("https://fixture.invalid/v1/") };
    private static bool IsSelection(JsonObject body) => body["tools"] is JsonArray { Count: 1 } tools &&
        tools[0]?["name"]?.GetValue<string>() == KnowledgeRelevance.FunctionName;
    private static JsonObject Selection(params string[] ids) => FakeModel.Function(KnowledgeRelevance.FunctionName,
        new JsonObject { ["relevantSourceIds"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) }
            .ToJsonString());
    private static KnowledgeHit Opened() => new(OpenedId, "Opened box", "Return an unused complete item.", 0.8);

    private sealed class RecordingKnowledge : IKnowledgeSearch
    {
        public List<string> Queries { get; } = [];
        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Queries.Add(query);
            return Task.FromResult<IReadOnlyList<KnowledgeHit>>(query == DefectQuestion
                ? [new(WarrantyId, "Warranty", "Report a defect to support for review.", 0.7)]
                : [Opened(), new("support-returns-period#001", "Return window", "Ten calendar days.", 0.6),
                    new("support-refund-timing#001", "Refund", "After inspection.", 0.5)]);
        }
    }

    private sealed class CaptureHandler(Func<JsonObject, JsonObject> respond) : HttpMessageHandler
    {
        public List<JsonObject> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Bodies.Add(body);
            var result = respond(body);
            result["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 2 };
            return new(HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
