using eShop.SupportApi;
using System.Text.Json.Nodes;

static class KnowledgeRetrievalTests
{
    private const string ReturnsQuestion =
        "Можно ли вернуть неиспользованную кофемолку с открытой коробкой?";
    private const string WarrantyQuestion =
        "И как действовать, если обнаружится неисправность?";
    private const string CompoundQuestion = ReturnsQuestion + " " + WarrantyQuestion +
        " Пока спрашиваю только об общих правилах.";

    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            checks++;
        }

        RunContext State(string question = CompoundQuestion, string mode = "workflow") =>
            new(new SupportRequest(question, null, false, Guid.NewGuid(), mode),
                "buyer-retrieval-test", "token-fixture");

        // Reproduce the live failure: the complete question returns three return policies,
        // while the defect question retrieves the missing warranty procedure.
        IReadOnlyList<KnowledgeHit> SearchFixture(string query) => query switch
        {
            WarrantyQuestion => [new("support-warranty#001", "Warranty",
                "Report a defect to support; replacement and refund require review.", 0.86)],
            ReturnsQuestion => [new("support-returns-opened#001", "Opened box",
                "An opened box alone does not prevent return of an unused complete item.", 0.80)],
            _ => [
                new("support-returns-opened#001", "Opened box", "Unused item policy.", 0.59),
                new("support-returns-period#001", "Return window", "Ten calendar days after receipt.", 0.44),
                new("support-refund-timing#001", "Refund timing", "After receipt and inspection.", 0.32)
            ]
        };

        foreach (string mode in new[] { "workflow", "agent" })
        {
            var state = State(mode: mode);
            var knowledge = new RecordingKnowledge(SearchFixture);
            var tickets = new FakeTickets();
            var tools = new SupportTools(knowledge, new FakeOrders(), tickets, state, new AcceptAllKnowledge());
            var model = new SourceAwareModel(state);
            var composer = new ReplyComposer(model);
            var reply = mode == "workflow"
                ? await new SupportWorkflow(composer).RunAsync(state, tools, default)
                : await new SupportAgent(model, composer).RunAsync(state, tools, default);

            Check(reply.Sources.Any(hit => hit.Id == "support-warranty#001") &&
                reply.Answer.Contains("[support-warranty#001]", StringComparison.Ordinal),
                $"{mode}: the missing warranty procedure reaches the answer context and citation.");
            Check(knowledge.Queries.SequenceEqual(
                new[] { CompoundQuestion, ReturnsQuestion, WarrantyQuestion }),
                $"{mode}: retrieval includes the complete question and both separate questions.");
            Check(reply.Sources.Count == 4 &&
                reply.Sources.Single(hit => hit.Id == "support-returns-opened#001").Score == 0.80,
                $"{mode}: merging preserves distinct results and the best duplicate score.");
            Check(!reply.UsedFallback && ReplyComposer.Validate(reply.Answer, state).Length == 0,
                $"{mode}: the merged facts produce a validated reply without fallback.");
            Check(model.Calls == (mode == "workflow" ? 1 : 2),
                $"{mode}: query decomposition adds no model planning call.");
            Check(tickets.Writes == 0 && reply.Ticket is null && state.Request.Question == CompoundQuestion,
                $"{mode}: a general policy question preserves the request and creates no ticket.");
        }

        var simple = State("When is the refund paid? I only want the policy.");
        var simpleKnowledge = new RecordingKnowledge(SearchFixture);
        var simpleTools = new SupportTools(simpleKnowledge, new FakeOrders(), new FakeTickets(), simple, new AcceptAllKnowledge());
        await simpleTools.SearchKnowledgeAsync(simple.Request.Question, default);
        Check(simpleKnowledge.Queries.SequenceEqual(new[] { simple.Request.Question }),
            "A single question plus explanatory text executes only one search.");

        var repeated = State("When is the refund paid? When is the refund paid?");
        var repeatedKnowledge = new RecordingKnowledge(SearchFixture);
        await new SupportTools(repeatedKnowledge, new FakeOrders(), new FakeTickets(), repeated, new AcceptAllKnowledge())
            .SearchKnowledgeAsync(repeated.Request.Question, default);
        Check(repeatedKnowledge.Queries.Count == 1,
            "Repeating the same question does not add duplicate searches.");

        var many = State("Question one? Question two? Question three? Question four?");
        var manyKnowledge = new RecordingKnowledge(SearchFixture);
        await new SupportTools(manyKnowledge, new FakeOrders(), new FakeTickets(), many, new AcceptAllKnowledge())
            .SearchKnowledgeAsync(many.Request.Question, default);
        Check(manyKnowledge.Queries.Count == 3 && manyKnowledge.Queries[0] == many.Request.Question,
            "A longer question list is bounded to three searches and retains the full query.");

        Check(KnowledgeQueryPlanner.Build("Return policy？ Warranty procedure？").Count == 3,
            "Question boundaries also support full-width question marks.");

        foreach (string invalid in new[] { " ", new string('x', 2001) })
        {
            var state = State();
            var knowledge = new RecordingKnowledge(SearchFixture);
            try
            {
                await new SupportTools(knowledge, new FakeOrders(), new FakeTickets(), state, new AcceptAllKnowledge())
                    .SearchKnowledgeAsync(invalid, default);
                throw new InvalidOperationException("An invalid query was accepted.");
            }
            catch (ArgumentException)
            {
                Check(knowledge.Queries.Count == 0, "An invalid query is rejected before retrieval.");
            }
        }

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var state = State();
            var knowledge = new RecordingKnowledge(SearchFixture);
            try
            {
                await new SupportTools(knowledge, new FakeOrders(), new FakeTickets(), state, new AcceptAllKnowledge())
                    .SearchKnowledgeAsync(CompoundQuestion, cancelled.Token);
                throw new InvalidOperationException("Cancellation was ignored.");
            }
            catch (OperationCanceledException)
            {
                Check(knowledge.Queries.Count == 0 && state.Sources.Count == 0,
                    "A pre-cancelled search calls no dependency and publishes no sources.");
            }
        }

        using (var cancelled = new CancellationTokenSource())
        {
            var state = State();
            int calls = 0;
            var knowledge = new RecordingKnowledge(query =>
            {
                if (++calls == 2) cancelled.Cancel();
                return SearchFixture(query);
            });
            try
            {
                await new SupportTools(knowledge, new FakeOrders(), new FakeTickets(), state, new AcceptAllKnowledge())
                    .SearchKnowledgeAsync(CompoundQuestion, cancelled.Token);
                throw new InvalidOperationException("Cancellation between queries was ignored.");
            }
            catch (OperationCanceledException)
            {
                Check(knowledge.Queries.Count == 2 && state.Sources.Count == 0,
                    "Cancellation stops remaining queries without publishing a partial source set.");
            }
        }

        Console.WriteLine("PASS: compound-question retrieval and source merging");
        return checks;
    }

    private sealed class RecordingKnowledge(Func<string, IReadOnlyList<KnowledgeHit>> search)
        : IKnowledgeSearch
    {
        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Queries.Add(query);
            return Task.FromResult(search(query));
        }
    }

    private sealed class SourceAwareModel(RunContext state) : IResponsesModel
    {
        public int Calls { get; private set; }

        public Task<JsonObject> RespondAsync(string instructions, IReadOnlyList<JsonNode> input,
            JsonArray? tools, CancellationToken ct, string? requiredFunction = null)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            if (Calls == 1 && tools is { Count: > 1 })
            {
                return Task.FromResult(FakeModel.Function("SearchKnowledge",
                    new JsonObject { ["query"] = state.Request.Question }.ToJsonString()));
            }

            // Only cite facts actually present in the context given to the composer.
            string text = "Unused item return policy [support-returns-opened#001].";
            if (state.Sources.Any(hit => hit.Id == "support-warranty#001"))
                text += " Report a defect to support for review [support-warranty#001].";
            return Task.FromResult(FakeModel.Text(text));
        }
    }
}
