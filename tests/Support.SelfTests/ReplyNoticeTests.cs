using eShop.SupportApi;
using System.Text.Json.Nodes;

static class ReplyNoticeTests
{
    // The model draft has valid evidence and procedure wording, but omits its
    // authority limit. Use a different chunk number from the live evaluation.
    private const string WarrantyId = "support-warranty#007";
    private const string OpenedId = "support-returns-opened#001";
    private const string Draft =
        "Открытая коробка не мешает возврату неиспользованного товара, если сохранены " +
        "товарный вид и комплект [support-returns-opened#001].\n\n" +
        "При неисправности обратитесь в поддержку с описанием проблемы [support-warranty#007].";
    private static readonly KnowledgeHit Warranty = new(WarrantyId, "Warranty",
        "При неисправности обратитесь в поддержку с описанием проблемы.", 0.7);
    private static readonly KnowledgeHit Opened = new(OpenedId, "Opened box",
        "Открытая коробка не мешает возврату неиспользованного товара.", 0.8);

    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: reply notices: " + message);
            checks++;
        }

        foreach (string mode in new[] { "workflow", "agent" })
        {
            var state = State(mode);
            var model = new NoticeModel(mode, Draft);
            var tickets = new FakeTickets();
            var reply = await RunAsync(state, model, new NoticeKnowledge([Opened, Warranty]), tickets);
            string notice = $"{ReplyNotices.WarrantyAuthority} [{WarrantyId}]";

            Check(reply.Answer.EndsWith(notice, StringComparison.Ordinal) &&
                Count(reply.Answer, ReplyNotices.WarrantyAuthority) == 1,
                $"{mode}: the omitted warranty authority limit is explicit in the final reply.");
            Check(reply.Answer.StartsWith(Draft, StringComparison.Ordinal) &&
                reply.Sources.Select(hit => hit.Id).SequenceEqual(new[] { OpenedId, WarrantyId }) &&
                ReplyComposer.Validate(reply.Answer, state).Length == 0,
                $"{mode}: assembly preserves the answer and uses a currently selected citation.");
            Check(model.Calls == (mode == "agent" ? 2 : 1) &&
                reply.Behavior is { ReplyRecomposed: false, AutoCompletedActions.Length: 0 },
                $"{mode}: adding the notice does not call the model again or report a draft repair.");
            Check(!reply.UsedFallback && tickets.Writes == 0 && reply.Ticket is null,
                $"{mode}: the mandatory notice is ordinary assembly and never creates a ticket.");
            Check(reply.Trace.Count(item => item == "reply notice warranty_authority") == 1,
                $"{mode}: the saved trace records application assembly before its snapshot.");

            var badState = State(mode);
            var badModel = new NoticeModel(mode, "Ответ без обязательной ссылки.");
            var fallback = await RunAsync(badState, badModel, new NoticeKnowledge([Warranty]), new FakeTickets());
            Check(fallback.UsedFallback && fallback.Answer.EndsWith(notice, StringComparison.Ordinal) &&
                ReplyComposer.Validate(fallback.Answer, badState).Length == 0,
                $"{mode}: two invalid drafts still produce a fallback with the authority limit.");
            Check(badModel.Calls == (mode == "agent" ? 3 : 2) &&
                fallback.Behavior is { ReplyRecomposed: true },
                $"{mode}: the notice does not add calls beyond the existing single repair attempt.");
        }

        // Reproduce the live failure: the model copied the application notice
        // without a citation, and Finish appended a second copy with a citation.
        string canonical = $"{ReplyNotices.WarrantyAuthority} [{WarrantyId}]";
        string wrapped = ReplyNotices.WarrantyAuthority.Replace(" Решение", "\nРешение", StringComparison.Ordinal);
        var endings = new[]
        {
            "\n\n" + ReplyNotices.WarrantyAuthority,
            " " + ReplyNotices.WarrantyAuthority,
            "\n\n" + wrapped,
            "\n\n" + ReplyNotices.WarrantyAuthority + $"[{WarrantyId}]",
            "\n\n" + ReplyNotices.WarrantyAuthority + "\n\n" + canonical,
            "\n\n" + canonical + "\n\n" + canonical,
            "\n\n" + ReplyNotices.WarrantyAuthority + " [support-warranty#008]",
            "\n\n" + canonical
        };
        foreach (string mode in new[] { "workflow", "agent" })
        {
            foreach (string ending in endings)
            {
                var state = State(mode);
                var model = new NoticeModel(mode, Draft + ending);
                var ticketStore = new FakeTickets();
                var reply = await RunAsync(state, model,
                    new NoticeKnowledge([Opened, Warranty, Warranty with { Id = "support-warranty#008" }]), ticketStore);
                Check(reply.Answer == Draft + "\n\n" + canonical &&
                    Count(reply.Answer, ReplyNotices.WarrantyAuthority) == 1 &&
                    ReplyComposer.Validate(reply.Answer, state).Length == 0,
                    $"{mode}: fixed trailing notice copies become one cited block without changing the procedure.");
                Check(model.Calls == (mode == "agent" ? 2 : 1) &&
                    reply.Behavior is { ReplyRecomposed: false } && !reply.UsedFallback && ticketStore.Writes == 0,
                    $"{mode}: duplicate cleanup adds no model call, repair, fallback or ticket write.");
            }
        }

        var formatting = State();
        formatting.AddSources([Opened, Warranty]);
        string quoted = Draft + "\n\nЦитата: «" + ReplyNotices.WarrantyAuthority + "»";
        Check(ReplyNotices.Append(quoted, formatting) == quoted + "\n\n" + canonical,
            "A quoted occurrence is preserved rather than treated as an application notice.");
        string unknownCitation = Draft + "\n\n" + ReplyNotices.WarrantyAuthority + " [support-warranty#999]";
        string stillInvalid = ReplyNotices.Append(unknownCitation, formatting);
        Check(stillInvalid.Contains("[support-warranty#999]", StringComparison.Ordinal) &&
            ReplyComposer.Validate(stillInvalid, formatting).Length > 0,
            "Normalization cannot conceal an unavailable citation from validation.");
        Check(ReplyNotices.Append(ReplyNotices.WarrantyAuthority, formatting) == canonical,
            "A notice-only reply receives its selected citation without a second notice or leading blank lines.");

        // A retrieved candidate is not a selected source. This also prevents a
        // rejected policy from leaking into an unrelated answer as a new citation.
        var filteredState = State();
        var filteredKnowledge = new NoticeKnowledge([Opened, Warranty]);
        var filteredModel = new NoticeModel("workflow", $"Правило открытой коробки [{OpenedId}].");
        var filteredTools = new SupportTools(filteredKnowledge, new FakeOrders(), new FakeTickets(),
            filteredState, new ExcludeWarranty());
        var filtered = await new SupportWorkflow(new ReplyComposer(filteredModel))
            .RunAsync(filteredState, filteredTools, default);
        Check(filtered.Sources.Count == 1 && filtered.Sources[0].Id == OpenedId &&
            !filtered.Answer.Contains(ReplyNotices.WarrantyAuthority, StringComparison.Ordinal) &&
            !filtered.Answer.Contains(WarrantyId, StringComparison.Ordinal),
            "A rejected warranty candidate adds neither a notice nor a citation.");

        var missing = State();
        missing.KnowledgeSearchCompleted = true;
        var missingReply = ReplyComposer.Finish(ReplyComposer.InsufficientKnowledgeMessage, missing, false);
        Check(missingReply.Answer == ReplyComposer.InsufficientKnowledgeMessage && missingReply.Sources.Count == 0,
            "Missing knowledge retains the citation-free insufficient-evidence reply.");

        foreach (string id in new[] { "support-warranty-extra#007", "support-warranty#not-a-chunk", "support-other#007" })
        {
            var unrelated = State();
            unrelated.AddSources([Warranty with { Id = id }]);
            var plain = ReplyComposer.Finish("Правило [" + id + "].", unrelated, false);
            Check(!plain.Answer.Contains(ReplyNotices.WarrantyAuthority, StringComparison.Ordinal),
                "A similar title, body or document-name prefix cannot register the warranty notice.");
        }

        var multiple = State();
        multiple.AddSources([Warranty, Warranty with { Id = "support-warranty#008" }]);
        var assembled = ReplyComposer.Finish(Draft, multiple, false);
        Check(Count(assembled.Answer, ReplyNotices.WarrantyAuthority) == 1 && assembled.Sources.Count == 2,
            "Multiple selected warranty chunks produce one notice while retaining both sources.");
        var assembledAgain = ReplyComposer.Finish(assembled.Answer, multiple, false);
        Check(assembledAgain.Answer == assembled.Answer &&
            multiple.Trace.Count(item => item == "reply notice warranty_authority") == 1,
            "An existing final notice is not duplicated or reported as newly added.");

        var selectedOrder = new RunContext(State().Request with { OrderId = 17, AllowTicket = true },
            "notice-test-buyer", "notice-test-token");
        selectedOrder.Order = new(true, new(17, DateTime.UtcNow, "Paid", 100), "OK");
        var ticket = new SupportTicket("T-0123456789abcdef0123456789abcdef", 17, "Review", DateTime.UtcNow);
        selectedOrder.Ticket = new(true, ticket, "Saved");
        selectedOrder.AddSources([Warranty]);
        var ticketReply = ReplyComposer.Finish("Заявка: " + ticket.Id + $" [{WarrantyId}].", selectedOrder, false);
        Check(ReferenceEquals(ticketReply.Ticket, selectedOrder.Ticket) &&
            ReferenceEquals(ticketReply.Order, selectedOrder.Order) &&
            ticketReply.Answer.Contains(ticket.Id, StringComparison.Ordinal) &&
            ticketReply.Answer.Contains(ReplyNotices.WarrantyAuthority, StringComparison.Ordinal),
            "Assembly preserves confirmed order and ticket facts.");

        var oldReply = new SupportReply(Draft, [Opened, Warranty], null, null, [], false);
        var oldTurn = new ConversationTurn(State().Request, oldReply, DateTime.UtcNow);
        var followUp = new RunContext(State().Request, "notice-test-buyer", "notice-test-token", [oldTurn]);
        followUp.AddSources([Opened]);
        var followUpReply = ReplyComposer.Finish($"Правило [{OpenedId}].", followUp, false);
        Check(!followUpReply.Answer.Contains(ReplyNotices.WarrantyAuthority, StringComparison.Ordinal),
            "Warranty evidence in history alone does not enable a notice on the current turn.");

        // Replays return their original snapshot, including replies produced
        // before mandatory notices were introduced.
        var replayModel = new NoticeModel("workflow", "Unexpected generation");
        var replayKnowledge = new NoticeKnowledge([Warranty]);
        var replayComposer = new ReplyComposer(replayModel);
        var replayService = new SupportConversationService(new ReplayOnlyStore(oldReply),
            new SupportAgent(replayModel, replayComposer), new SupportWorkflow(replayComposer),
            new AcceptAllKnowledge());
        var replay = await replayService.AskAsync(State().Request, "notice-test-buyer", "notice-test-token",
            replayKnowledge, new FakeOrders(), new FakeTickets(), default);
        Check(replay.Answer == oldReply.Answer && replay.Sources.SequenceEqual(oldReply.Sources) &&
            !replay.Trace.Contains("reply notice warranty_authority", StringComparer.Ordinal),
            "Replay preserves the saved answer and sources without retroactively adding a notice.");
        Check(replayModel.Calls == 0 && replayKnowledge.Calls == 0,
            "Replay performs no model generation or retrieval for the new assembly rule.");

        Console.WriteLine("PASS: mandatory warranty notice in agent, workflow, fallback and replay");
        return checks;
    }

    private static RunContext State(string mode = "workflow") =>
        new(new SupportRequest("Какие условия возврата и обращения при неисправности?",
            null, false, Guid.NewGuid(), mode), "notice-test-buyer", "notice-test-token");

    private static async Task<SupportReply> RunAsync(RunContext state, IResponsesModel model,
        IKnowledgeSearch knowledge, FakeTickets tickets)
    {
        var tools = new SupportTools(knowledge, new FakeOrders(), tickets, state, new AcceptAllKnowledge());
        var composer = new ReplyComposer(model);
        return state.Request.Mode == "agent"
            ? await new SupportAgent(model, composer).RunAsync(state, tools, default)
            : await new SupportWorkflow(composer).RunAsync(state, tools, default);
    }

    private static int Count(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;

    private sealed class NoticeModel(string mode, string draft) : IResponsesModel
    {
        public int Calls { get; private set; }
        public Task<JsonObject> RespondAsync(string instructions, IReadOnlyList<JsonNode> input,
            JsonArray? tools, CancellationToken ct, string? requiredFunction = null)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(mode == "agent" && Calls == 1
                ? FakeModel.Function("SearchKnowledge", """{"query":"возврат и неисправность"}""")
                : FakeModel.Text(draft));
        }
    }

    private sealed class NoticeKnowledge(IReadOnlyList<KnowledgeHit> hits) : IKnowledgeSearch
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(hits);
        }
    }

    private sealed class ExcludeWarranty : IKnowledgeRelevance
    {
        public Task<IReadOnlyList<KnowledgeHit>> SelectAsync(string question, string query,
            IReadOnlyList<KnowledgeHit> candidates, IReadOnlyList<ConversationTurn> history, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<KnowledgeHit>>(candidates.Where(hit => hit.Id == OpenedId).ToArray());
    }

    private sealed class ReplayOnlyStore(SupportReply reply) : IConversationStore
    {
        public Task<IConversationLease> OpenAsync(SupportRequest request, string userId, CancellationToken ct) =>
            Task.FromResult<IConversationLease>(new Lease(reply));
        public Task<ConversationView?> GetAsync(Guid id, string userId, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected conversation read during replay.");
        public Task<ConversationSummary[]> ListAsync(string userId, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected conversation list during replay.");

        private sealed class Lease(SupportReply reply) : IConversationLease
        {
            public IReadOnlyList<ConversationTurn> History => [];
            public SupportReply? CachedReply => reply;
            public Task SaveAsync(SupportReply result, CancellationToken ct) =>
                throw new InvalidOperationException("Replay must not save a changed answer.");
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
