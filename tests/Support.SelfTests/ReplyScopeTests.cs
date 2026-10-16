using eShop.SupportApi;
using System.Text.Json.Nodes;

static class ReplyScopeTests
{
    // Different order and chunk IDs from the evaluation fixtures prevent a test
    // from passing through hard-coded live scenario values.
    private const string PeriodId = "support-returns-period#009";
    private const string DeliveryId = "support-delivery-delay#006";
    private const string WarrantyId = "support-warranty#007";
    private static readonly KnowledgeHit Period = new(PeriodId, "Return window",
        "Неиспользованный товар можно вернуть в течение десяти календарных дней после получения. " +
        "Это правило учебного магазина, а не изложение закона о защите прав потребителей.", 0.8);
    private static readonly KnowledgeHit Delivery = new(DeliveryId, "Delivery inquiry",
        "Покупатель может попросить уточнить срок доставки через заявку.", 0.8);
    private static readonly KnowledgeHit Warranty = new(WarrantyId, "Warranty",
        "При неисправности обратитесь в поддержку с описанием проблемы.", 0.7);

    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: reply scope: " + message);
            checks++;
        }

        foreach (string mode in new[] { "agent", "workflow" })
        {
            // The draft reproduces the omission seen in live evaluation: it has
            // the actual ticket ID and a valid citation, but no outcome boundary.
            var saved = State(mode);
            var store = new FakeTickets();
            string TicketDraft() => "Заказ 17: Paid. Заявка сохранена: " + saved.Ticket!.Ticket!.Id +
                $" [{DeliveryId}].";
            var ticketModel = new ScopeModel(TicketDraft, needsTicket: true);
            var ticketReply = await RunAsync(saved, ticketModel, [Delivery], store);
            Check(ticketReply.Answer == TicketDraft() + "\n\n" + ReplyNotices.TicketOutcome &&
                store.Writes == 1 && ticketReply.Ticket is { Created: true, Ticket: not null },
                $"{mode}: a confirmed write has an explicit delivery and compensation boundary, with the real ID preserved.");
            Check(ticketReply.Answer.Contains("не устанавливает дату доставки", StringComparison.Ordinal) &&
                ticketReply.Answer.Contains("не одобряет компенсацию, отмену заказа или возврат денег", StringComparison.Ordinal) &&
                ticketReply.Answer.Contains("не отправлена во внешнюю службу", StringComparison.Ordinal),
                $"{mode}: the ticket notice states the required limits rather than merely reporting a successful write.");
            Check(ticketModel.Calls == (mode == "agent" ? 4 : 2) &&
                !ticketReply.UsedFallback && ticketReply.Behavior is { ReplyRecomposed: false },
                $"{mode}: appending the ticket boundary adds no model call or repair.");
            Check(ReplyComposer.Validate(ticketReply.Answer, saved).Length == 0 &&
                ticketReply.Trace.Count(item => item == "reply notice ticket_outcome") == 1 &&
                ticketReply.Sources.Select(hit => hit.Id).SequenceEqual(new[] { DeliveryId }),
                $"{mode}: the boundary is recorded before persistence without inventing a source.");

            var policy = State(mode);
            string PolicyDraft() => $"Неиспользованный товар можно вернуть в течение десяти календарных дней [{PeriodId}].\n\n" +
                TicketPolicy.NoTicketCreated;
            var policyModel = new ScopeModel(PolicyDraft);
            var noTicketStore = new FakeTickets();
            var policyReply = await RunAsync(policy, policyModel, [Period], noTicketStore);
            Check(policyReply.Answer == PolicyDraft() + $"\n\n{ReplyNotices.ReturnPolicyScope} [{PeriodId}]" &&
                !policyReply.Answer.Contains(ReplyNotices.TicketOutcome, StringComparison.Ordinal),
                $"{mode}: a return-window answer distinguishes store policy from consumer law using the selected chunk.");
            Check(policyReply.Answer.Contains("правило учебного магазина", StringComparison.Ordinal) &&
                policyReply.Answer.Contains("не изложение закона о защите прав потребителей", StringComparison.Ordinal),
                $"{mode}: mentioning the demo store alone cannot replace the explicit consumer-law distinction.");
            Check(noTicketStore.Writes == 0 && policyReply.Ticket is null &&
                policyModel.Calls == (mode == "agent" ? 3 : 2) && !policyReply.UsedFallback,
                $"{mode}: checked permission does not turn a policy answer into a ticket write.");
            Check(ReplyComposer.Validate(policyReply.Answer, policy).Length == 0 &&
                policyReply.Trace.Contains("reply notice return_policy_scope", StringComparer.Ordinal),
                $"{mode}: the policy boundary reaches the final validated snapshot.");

            foreach (var deniedRequest in new[]
            {
                State(mode).Request with { AllowTicket = false },
                State(mode).Request with { OrderId = 999 }
            })
            {
                var denied = new RunContext(deniedRequest, "scope-buyer", "scope-token");
                var deniedStore = new FakeTickets();
                var deniedModel = new ScopeModel(() => $"Правило [{DeliveryId}]. " + TicketPolicy.NoTicketCreated,
                    needsTicket: true);
                var deniedReply = await RunAsync(denied, deniedModel, [Delivery], deniedStore);
                Check(deniedStore.Writes == 0 && deniedReply.Ticket is not { Created: true } &&
                    !deniedReply.Answer.Contains(ReplyNotices.TicketOutcome, StringComparison.Ordinal),
                    $"{mode}: permission and ownership failures cannot activate wording that claims a saved ticket.");
                Check(!deniedReply.UsedFallback && ReplyComposer.Validate(deniedReply.Answer, denied).Length == 0,
                    $"{mode}: the ordinary denial path remains valid after notice assembly.");
            }

            // A successful local write can coexist with no relevant KB evidence.
            // Both the canonical draft and its final form must stay strictly grounded.
            var missing = State(mode);
            var missingStore = new FakeTickets();
            var missingModel = new ScopeModel(() => "Unexpected draft", needsTicket: true);
            var missingReply = await RunAsync(missing, missingModel, [], missingStore);
            Check(missingStore.Writes == 1 && missingReply.Sources.Count == 0 &&
                missingReply.Answer.Contains(missing.Ticket!.Ticket!.Id, StringComparison.Ordinal) &&
                missingReply.Answer.Contains(ReplyComposer.InsufficientKnowledgeMessage, StringComparison.Ordinal) &&
                missingReply.Answer.EndsWith(ReplyNotices.TicketOutcome, StringComparison.Ordinal) &&
                !missingReply.Answer.Contains('['),
                $"{mode}: no-evidence replies preserve the confirmed ticket, boundary and evidence limitation without citations.");
            Check(missingModel.Calls == (mode == "agent" ? 4 : 1) && !missingReply.UsedFallback,
                $"{mode}: missing evidence adds no text-generation call for the mandatory boundary.");
            int traceCount = missing.Trace.Count;
            Check(ReplyComposer.Validate(missingReply.Answer, missing).Length == 0 && missing.Trace.Count == traceCount,
                $"{mode}: validating the formatted confirmed-data reply has no trace side effects.");
            foreach (string unsupported in new[]
            {
                missingReply.Answer + "\nДоставка будет завтра.",
                "Компенсация одобрена.\n" + missingReply.Answer,
                missingReply.Answer.Replace(ReplyComposer.InsufficientKnowledgeMessage, "", StringComparison.Ordinal),
                missingReply.Answer + " [support-delivery-delay#999]"
            })
                Check(ReplyComposer.Validate(unsupported, missing).Length > 0,
                    $"{mode}: mandatory wording cannot make an altered no-evidence reply valid.");

            var fallback = State(mode);
            var fallbackModel = new ScopeModel(() => "Ответ без номера и источников.", needsTicket: true);
            var fallbackStore = new FakeTickets();
            var fallbackReply = await RunAsync(fallback, fallbackModel, [Period, Delivery], fallbackStore);
            Check(fallbackReply.UsedFallback && fallbackStore.Writes == 1 &&
                fallbackReply.Answer.Contains(fallback.Ticket!.Ticket!.Id, StringComparison.Ordinal) &&
                Count(fallbackReply.Answer, ReplyNotices.ReturnPolicyScope) == 1 &&
                Count(fallbackReply.Answer, ReplyNotices.TicketOutcome) == 1 &&
                ReplyComposer.Validate(fallbackReply.Answer, fallback).Length == 0,
                $"{mode}: fallback retains the one confirmed write and both required boundaries.");
            Check(fallbackModel.Calls == (mode == "agent" ? 5 : 3),
                $"{mode}: fallback adds only the existing single repair call.");
        }

        var combined = State("workflow");
        combined.Ticket = new(true, new("T-0123456789abcdef0123456789abcdef", 17, "Inquiry", DateTime.UtcNow), "Saved");
        combined.AddSources([Period, Warranty, Period with { Id = "support-returns-period#010" }]);
        string body = $"Заявка: {combined.Ticket.Ticket!.Id}. Правила [{PeriodId}] [{WarrantyId}].";
        string canonical = body + $"\n\n{ReplyNotices.WarrantyAuthority} [{WarrantyId}]" +
            $"\n\n{ReplyNotices.ReturnPolicyScope} [{PeriodId}]\n\n{ReplyNotices.TicketOutcome}";
        foreach (string suffix in new[]
        {
            "",
            "\n\n" + ReplyNotices.TicketOutcome + "\n\n" + ReplyNotices.ReturnPolicyScope +
                "\n\n" + ReplyNotices.WarrantyAuthority,
            $"\n\n{ReplyNotices.ReturnPolicyScope} [support-returns-period#010]\n\n" +
                ReplyNotices.WarrantyAuthority.Replace(" Решение", "\nРешение", StringComparison.Ordinal) +
                "\n\n" + ReplyNotices.TicketOutcome + "\n\n" + ReplyNotices.ReturnPolicyScope
        })
        {
            var normalized = ReplyComposer.Finish(body + suffix, combined, false);
            Check(normalized.Answer == canonical && ReplyComposer.Validate(normalized.Answer, combined).Length == 0,
                "Interleaved literal copies of all active boundaries become one block each, preserving the substantive answer.");
            int traceCount = combined.Trace.Count;
            Check(ReplyComposer.Finish(normalized.Answer, combined, false).Answer == normalized.Answer &&
                combined.Trace.Count == traceCount,
                "Formatting all active boundaries is idempotent and a second pass logs no new assembly.");
        }
        string citedTicket = body + $"\n\n{ReplyNotices.TicketOutcome} [{PeriodId}]";
        string preserved = ReplyComposer.Finish(citedTicket, combined, false).Answer;
        Check(Count(preserved, ReplyNotices.TicketOutcome) == 1 &&
            preserved.EndsWith($"{ReplyNotices.TicketOutcome} [{PeriodId}]", StringComparison.Ordinal) &&
            ReplyComposer.Validate(preserved, combined).Length == 0,
            "A valid citation on a copied tool boundary is preserved rather than silently deleted.");
        foreach (string notice in new[] { ReplyNotices.ReturnPolicyScope, ReplyNotices.TicketOutcome })
        {
            string unknown = body + "\n\n" + notice + " [support-unknown#999]";
            string final = ReplyComposer.Finish(unknown, combined, false).Answer;
            Check(final.Contains("[support-unknown#999]", StringComparison.Ordinal) &&
                ReplyComposer.Validate(final, combined).Length > 0,
                "Boundary normalization cannot hide an unknown citation.");
            string quoted = body + "\n\nЦитата: «" + notice + "»";
            Check(ReplyComposer.Finish(quoted, combined, false).Answer.StartsWith(quoted, StringComparison.Ordinal),
                "Quoted fixed wording is preserved as content rather than removed as a footer.");
        }

        foreach (string id in new[] { "support-returns-period-extra#009", "support-returns-period#000",
            "support-returns-period#bad", "support-other#009" })
        {
            var unrelated = State("workflow");
            unrelated.AddSources([Period with { Id = id }]);
            var reply = ReplyComposer.Finish($"Правило [{id}].", unrelated, false);
            Check(!reply.Answer.Contains(ReplyNotices.ReturnPolicyScope, StringComparison.Ordinal),
                "A similar document name, title or body cannot register the return-window notice.");
        }
        foreach (var result in new TicketResult?[] { null, new(false, combined.Ticket.Ticket, "Not created"), new(true, null, "Incomplete") })
        {
            var unconfirmed = State("workflow");
            unconfirmed.Ticket = result;
            Check(!ReplyComposer.Finish("Ответ.", unconfirmed, false).Answer.Contains(ReplyNotices.TicketOutcome, StringComparison.Ordinal),
                "Only a confirmed result with an actual current ticket activates the saved-ticket boundary.");
        }

        var rejected = State("workflow");
        var rejectedStore = new FakeTickets();
        var rejectedModel = new ScopeModel(() => $"Правило [{DeliveryId}]. " + TicketPolicy.NoTicketCreated);
        var rejectedReply = await RunAsync(rejected, rejectedModel, [Period, Delivery], rejectedStore, new ExcludePeriod());
        Check(rejectedReply.Sources.All(hit => hit.Id != PeriodId) &&
            !rejectedReply.Answer.Contains(ReplyNotices.ReturnPolicyScope, StringComparison.Ordinal) && rejectedStore.Writes == 0,
            "A rejected return-window candidate adds neither its notice nor a ticket.");

        var oldReply = new SupportReply("Ранее сохранённый ответ без новых пояснений.", [Period], null, combined.Ticket, [], false);
        var request = State("workflow").Request;
        var history = new[] { new ConversationTurn(request, oldReply, DateTime.UtcNow) };
        var current = new RunContext(request, "scope-buyer", "scope-token", history);
        current.AddSources([Delivery]);
        var currentReply = ReplyComposer.Finish($"Правило [{DeliveryId}]. " + TicketPolicy.NoTicketCreated, current, false);
        Check(!currentReply.Answer.Contains(ReplyNotices.ReturnPolicyScope, StringComparison.Ordinal) &&
            !currentReply.Answer.Contains(ReplyNotices.TicketOutcome, StringComparison.Ordinal),
            "Policy evidence and a saved ticket in history do not activate current-turn boundaries.");

        var replayModel = new ScopeModel(() => throw new InvalidOperationException("Replay called the model."));
        var replayComposer = new ReplyComposer(replayModel);
        var replayStore = new FakeTickets();
        var replayService = new SupportConversationService(new ReplayStore(oldReply),
            new SupportAgent(replayModel, replayComposer), new SupportWorkflow(replayComposer), new AcceptAllKnowledge());
        var replay = await replayService.AskAsync(request, "scope-buyer", "scope-token",
            new FakeKnowledge(), new FakeOrders(), replayStore, default);
        Check(replay.Answer == oldReply.Answer && replay.Sources.SequenceEqual(oldReply.Sources) &&
            replay.Ticket == oldReply.Ticket && replayModel.Calls == 0 && replayStore.Writes == 0,
            "Replay preserves the original answer and confirmed ticket without adding notices, generation or writes.");

        Console.WriteLine("PASS: ticket outcome and demo policy boundaries in agent, workflow, fallback and replay");
        return checks;
    }

    private static RunContext State(string mode) =>
        new(new SupportRequest("Уточни правила по выбранному заказу.", 17, true, Guid.NewGuid(), mode),
            "scope-buyer", "scope-token");

    private static async Task<SupportReply> RunAsync(RunContext state, IResponsesModel model,
        IReadOnlyList<KnowledgeHit> hits, FakeTickets tickets, IKnowledgeRelevance? relevance = null)
    {
        var tools = new SupportTools(new ScopeKnowledge(hits), new FakeOrders(), tickets, state,
            relevance ?? new AcceptAllKnowledge());
        var composer = new ReplyComposer(model);
        return state.Request.Mode == "agent"
            ? await new SupportAgent(model, composer).RunAsync(state, tools, default)
            : await new SupportWorkflow(composer).RunAsync(state, tools, default);
    }

    private static int Count(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;

    private sealed class ScopeModel(Func<string> draft, bool needsTicket = false) : IResponsesModel
    {
        public int Calls { get; private set; }
        private int agentStep;
        public Task<JsonObject> RespondAsync(string instructions, IReadOnlyList<JsonNode> input,
            JsonArray? tools, CancellationToken ct, string? requiredFunction = null)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            if (requiredFunction == TicketPolicy.DecisionFunction)
                return Task.FromResult(FakeModel.Function(TicketPolicy.DecisionFunction,
                    needsTicket ? """{"needsTicket":true,"reason":"buyer_request"}""" :
                        """{"needsTicket":false,"reason":"answered_by_policy"}"""));
            if (tools is { Count: > 1 })
            {
                agentStep++;
                if (agentStep == 1)
                    return Task.FromResult(FakeModel.Function("GetOrderStatus", "{}"));
                if (agentStep == 2)
                    return Task.FromResult(FakeModel.Function("SearchKnowledge", """{"query":"правила магазина"}"""));
                if (agentStep == 3 && needsTicket)
                    return Task.FromResult(FakeModel.Function("CreateSupportTicket", """{"summary":"Уточнить срок доставки"}"""));
            }
            return Task.FromResult(FakeModel.Text(draft()));
        }
    }

    private sealed class ScopeKnowledge(IReadOnlyList<KnowledgeHit> hits) : IKnowledgeSearch
    {
        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, CancellationToken ct) => Task.FromResult(hits);
    }

    private sealed class ExcludePeriod : IKnowledgeRelevance
    {
        public Task<IReadOnlyList<KnowledgeHit>> SelectAsync(string question, string query,
            IReadOnlyList<KnowledgeHit> candidates, IReadOnlyList<ConversationTurn> history, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<KnowledgeHit>>(candidates.Where(hit => hit.Id != PeriodId).ToArray());
    }

    private sealed class ReplayStore(SupportReply saved) : IConversationStore
    {
        public Task<IConversationLease> OpenAsync(SupportRequest request, string userId, CancellationToken ct) =>
            Task.FromResult<IConversationLease>(new Lease(saved));
        public Task<ConversationView?> GetAsync(Guid id, string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConversationSummary[]> ListAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        private sealed class Lease(SupportReply saved) : IConversationLease
        {
            public IReadOnlyList<ConversationTurn> History => [];
            public SupportReply? CachedReply => saved;
            public Task SaveAsync(SupportReply reply, CancellationToken ct) => throw new InvalidOperationException("Replay changed the snapshot.");
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
