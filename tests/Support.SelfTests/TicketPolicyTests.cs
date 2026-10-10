using eShop.SupportApi;
using System.Text.Json.Nodes;

static class TicketPolicyTests
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + message);
            checks++;
        }

        RunContext Context(bool allow = true, int? order = 17,
            string question = "Объясни правила доставки.",
            IReadOnlyList<ConversationTurn>? history = null, string mode = "workflow") =>
            new(new SupportRequest(question, order, allow, Guid.NewGuid(), mode),
                "buyer-test", "token-fixture", history);

        SupportTools Tools(RunContext context, FakeTickets tickets) =>
            new(new FakeKnowledge(), new FakeOrders(), tickets, context);

        string NoTicketReply() =>
            "Правило [support-delivery#001]. " + TicketPolicy.NoTicketCreated;

        // Permission does not itself request a write.
        var policy = Context();
        var policyTickets = new FakeTickets();
        var policyModel = new ProbeModel((_, _, tools) => IsDecision(tools)
            ? Decision(false, "answered_by_policy") : FakeModel.Text(NoTicketReply()));
        var policyReply = await new SupportWorkflow(new ReplyComposer(policyModel))
            .RunAsync(policy, Tools(policy, policyTickets), default);
        Check(policyTickets.Writes == 0 && policyReply.Ticket is null,
            "Permission plus a policy answer does not create a ticket.");
        Check(policyModel.DecisionCalls == 1 && !policyReply.UsedFallback,
            "An eligible workflow evaluates the decision once and returns a valid reply.");
        Check(policyReply.Answer.Contains(TicketPolicy.NoTicketCreated),
            "A reply explicitly reports that no new ticket was created.");

        // Repairing text must not turn a no-ticket decision into a write.
        var repair = Context();
        var repairTickets = new FakeTickets();
        var repairModel = new ProbeModel((step, _, tools) => IsDecision(tools)
            ? Decision(false, "answered_by_policy")
            : FakeModel.Text(step == 2 ? "Правило [support-delivery#001]." : NoTicketReply()));
        var repaired = await new SupportWorkflow(new ReplyComposer(repairModel))
            .RunAsync(repair, Tools(repair, repairTickets), default);
        Check(repairTickets.Writes == 0 && repairModel.Calls == 3 && !repaired.UsedFallback,
            "A missing no-ticket statement is repaired without a ticket write.");
        Check(repaired.Answer.Contains(TicketPolicy.NoTicketCreated),
            "The repaired reply explicitly reports that no new ticket was created.");

        var fallbackState = Context();
        var fallbackTickets = new FakeTickets();
        var fallbackModel = new ProbeModel((_, _, tools) => IsDecision(tools)
            ? Decision(false, "answered_by_policy")
            : FakeModel.Text("Правило [support-delivery#001]."));
        var fallbackReply = await new SupportWorkflow(new ReplyComposer(fallbackModel))
            .RunAsync(fallbackState, Tools(fallbackState, fallbackTickets), default);
        Check(fallbackReply.UsedFallback && fallbackTickets.Writes == 0 &&
            fallbackReply.Answer.Contains(TicketPolicy.NoTicketCreated),
            "The fallback preserves the no-ticket outcome after two invalid drafts.");

        // Both positive reasons execute the existing guarded business tool.
        foreach (var reason in new[] { "buyer_request", "human_action_required" })
        {
            var state = Context(question: reason == "buyer_request"
                ? "Создай заявку по заказу."
                : "Служба доставки не смогла связаться со мной; нужна помощь.");
            var tickets = new FakeTickets();
            var model = new ProbeModel((_, _, tools) => IsDecision(tools)
                ? Decision(true, reason)
                : FakeModel.Text("Правило [support-delivery#001]. Заявка: " +
                    state.Ticket!.Ticket!.Id));
            var reply = await new SupportWorkflow(new ReplyComposer(model))
                .RunAsync(state, Tools(state, tickets), default);
            Check(tickets.Writes == 1 && reply.Ticket is { Created: true },
                $"The workflow creates one ticket for {reason}.");
            Check(model.DecisionCalls == 1 && !reply.UsedFallback,
                $"The workflow returns a validated reply for {reason}.");
        }

        var unclear = Context(question: "Разберитесь с этим.");
        var unclearTickets = new FakeTickets();
        var unclearModel = new ProbeModel((_, _, tools) => IsDecision(tools)
            ? Decision(false, "insufficient_context") : FakeModel.Text(NoTicketReply()));
        await new SupportWorkflow(new ReplyComposer(unclearModel))
            .RunAsync(unclear, Tools(unclear, unclearTickets), default);
        Check(unclearTickets.Writes == 0,
            "An insufficient-context decision does not execute a write.");

        // Ineligible requests do not send a ticket-decision request to the model.
        foreach (var (allow, order) in new (bool, int?)[]
            { (false, 17), (true, 999), (true, null) })
        {
            var state = Context(allow, order, "Создай заявку.");
            var tickets = new FakeTickets();
            var model = new ProbeModel((_, _, _) => FakeModel.Text(NoTicketReply()));
            var reply = await new SupportWorkflow(new ReplyComposer(model))
                .RunAsync(state, Tools(state, tickets), default);
            Check(tickets.Writes == 0 && reply.Ticket is null,
                $"An ineligible workflow does not write: allow={allow}, order={order}.");
            Check(model.DecisionCalls == 0 && !reply.UsedFallback,
                $"An ineligible workflow skips model classification: allow={allow}, order={order}.");
        }

        // A past permission or ticket must not become authority for this message.
        var oldTicket = new SupportTicket("T-11111111111111111111111111111111",
            17, "Earlier request", DateTime.UtcNow);
        var oldRequest = new SupportRequest("Создай заявку.", 17, true, Guid.NewGuid());
        var history = new[]
        {
            new ConversationTurn(oldRequest,
                new SupportReply("Заявка: " + oldTicket.Id, [], null,
                    new(true, oldTicket, "Saved"), [], false), DateTime.UtcNow)
        };
        var current = Context(allow: false, history: history);
        var currentTickets = new FakeTickets();
        var currentModel = new ProbeModel((_, _, _) => FakeModel.Text(NoTicketReply()));
        await new SupportWorkflow(new ReplyComposer(currentModel))
            .RunAsync(current, Tools(current, currentTickets), default);
        Check(currentTickets.Writes == 0 && currentModel.DecisionCalls == 0,
            "History cannot grant current-message permission.");

        // Malformed, ambiguous and contradictory decisions must fail before writing.
        var malformed = new List<JsonObject>
        {
            FakeModel.Text("No decision call"),
            FakeModel.Function("UnknownDecision", "{}"),
            FakeModel.Function(TicketPolicy.DecisionFunction, "not json"),
            FakeModel.Function(TicketPolicy.DecisionFunction, "[]"),
            FakeModel.Function(TicketPolicy.DecisionFunction,
                """{"needsTicket":"true","reason":"buyer_request"}"""),
            FakeModel.Function(TicketPolicy.DecisionFunction,
                """{"needsTicket":true,"reason":"answered_by_policy"}"""),
            FakeModel.Function(TicketPolicy.DecisionFunction,
                """{"needsTicket":false,"reason":"buyer_request"}"""),
            FakeModel.Function(TicketPolicy.DecisionFunction,
                """{"needsTicket":true,"reason":"unknown"}"""),
            FakeModel.Function(TicketPolicy.DecisionFunction,
                """{"needsTicket":true,"reason":"buyer_request","orderId":999}""")
        };
        var missingArguments = Decision(true, "buyer_request");
        missingArguments["output"]![0]!.AsObject().Remove("arguments");
        malformed.Add(missingArguments);
        var duplicateCall = Decision(true, "buyer_request");
        duplicateCall["output"]!.AsArray().Add(
            duplicateCall["output"]![0]!.DeepClone());
        malformed.Add(duplicateCall);

        foreach (var invalid in malformed)
        {
            var state = Context();
            var tickets = new FakeTickets();
            var model = new ProbeModel((_, _, _) => invalid.DeepClone().AsObject());
            bool rejected = false;
            try
            {
                await new SupportWorkflow(new ReplyComposer(model))
                    .RunAsync(state, Tools(state, tickets), default);
            }
            catch (Exception error) when (
                error is UpstreamException || error.InnerException is UpstreamException)
            {
                rejected = true;
            }
            Check(rejected && tickets.Writes == 0,
                "An invalid model decision is rejected without a ticket write.");
        }

        // The agent may decline creation even with permission; C# does not complete it.
        var agentState = Context(mode: "agent");
        var agentTickets = new FakeTickets();
        var agentModel = new ProbeModel((step, _, _) => step switch
        {
            1 => FakeModel.Function("GetOrderStatus", "{}"),
            2 => FakeModel.Function("SearchKnowledge", """{"query":"доставка"}"""),
            3 => FakeModel.Text(NoTicketReply()),
            _ => throw new InvalidOperationException("Unexpected agent model call.")
        });
        var agentReply = await new SupportAgent(agentModel, new ReplyComposer(agentModel))
            .RunAsync(agentState, Tools(agentState, agentTickets), default);
        Check(agentTickets.Writes == 0 && agentReply.Ticket is null && !agentReply.UsedFallback,
            "The agent can answer a policy question without creating an allowed ticket.");
        Check(agentModel.Calls == 3,
            "A completed agent response does not trigger automatic ticket creation or recomposition.");

        // The agent's explicit call still executes the same guarded C# tool.
        var writeState = Context(question: "Создай заявку.", mode: "agent");
        var writeTickets = new FakeTickets();
        var writeModel = new ProbeModel((step, _, _) => step switch
        {
            1 => FakeModel.Function("GetOrderStatus", "{}"),
            2 => FakeModel.Function("SearchKnowledge", """{"query":"доставка"}"""),
            3 => FakeModel.Function("CreateSupportTicket", """{"summary":"Уточнить доставку"}"""),
            4 => FakeModel.Text("Правило [support-delivery#001]. Заявка: " +
                writeState.Ticket!.Ticket!.Id),
            _ => throw new InvalidOperationException("Unexpected agent model call.")
        });
        var writeReply = await new SupportAgent(writeModel, new ReplyComposer(writeModel))
            .RunAsync(writeState, Tools(writeState, writeTickets), default);
        Check(writeTickets.Writes == 1 && writeReply.Ticket is { Created: true } && !writeReply.UsedFallback,
            "The agent creates a ticket only after an explicit model tool call.");

        var deniedAgent = Context(allow: false, mode: "agent");
        var deniedTickets = new FakeTickets();
        var deniedModel = new ProbeModel((step, _, _) => step switch
        {
            1 => FakeModel.Function("GetOrderStatus", "{}"),
            2 => FakeModel.Function("SearchKnowledge", """{"query":"доставка"}"""),
            3 => FakeModel.Function("CreateSupportTicket", """{"summary":"Уточнить доставку"}"""),
            4 => FakeModel.Text(NoTicketReply()),
            _ => throw new InvalidOperationException("Unexpected agent model call.")
        });
        await new SupportAgent(deniedModel, new ReplyComposer(deniedModel))
            .RunAsync(deniedAgent, Tools(deniedAgent, deniedTickets), default);
        Check(deniedTickets.Writes == 0 && deniedAgent.Ticket is { Created: false },
            "A model tool call cannot bypass missing permission.");

        // Required reads remain mandatory even when the agent skips every tool.
        var omitted = Context(mode: "agent");
        var omittedTickets = new FakeTickets();
        var omittedModel = new ProbeModel((step, _, _) => FakeModel.Text(step == 1
            ? TicketPolicy.NoTicketCreated : NoTicketReply()));
        var omittedReply = await new SupportAgent(omittedModel, new ReplyComposer(omittedModel))
            .RunAsync(omitted, Tools(omitted, omittedTickets), default);
        Check(omitted.Order is { Found: true } && omitted.Sources.Count > 0 && omittedTickets.Writes == 0,
            "C# completes required reads without completing a ticket write.");
        Check(omittedModel.Calls == 2 && !omittedReply.UsedFallback,
            "A missing-facts response is recomposed once after required reads.");

        Check(ReplyComposer.Validate("Правило [support-delivery#001].", policy).Length > 0,
            "A reply cannot omit the no-ticket statement when permission was given.");
        Check(ReplyComposer.Validate(NoTicketReply(), policy).Length == 0,
            "The explicit no-ticket statement passes validation.");
        var withTicket = writeState.Ticket!.Ticket!.Id;
        Check(ReplyComposer.Validate(NoTicketReply() + " Заявка: " + withTicket, writeState).Length > 0,
            "A created ticket cannot be described as not created.");
        var fallback = ReplyComposer.Finish("", policy, true);
        Check(fallback.Answer.Contains(TicketPolicy.NoTicketCreated),
            "The fallback explicitly reports that no new ticket was created.");

        return checks;
    }

    private static bool IsDecision(JsonArray? tools) =>
        tools is { Count: 1 } &&
        tools[0]?["name"]?.GetValue<string>() == TicketPolicy.DecisionFunction;

    private static JsonObject Decision(bool needed, string reason) =>
        FakeModel.Function(TicketPolicy.DecisionFunction, new JsonObject
        {
            ["needsTicket"] = needed,
            ["reason"] = reason
        }.ToJsonString());

    private sealed class ProbeModel(
        Func<int, IReadOnlyList<JsonNode>, JsonArray?, JsonObject> response) : IResponsesModel
    {
        public int Calls { get; private set; }
        public int DecisionCalls { get; private set; }

        public Task<JsonObject> RespondAsync(string instructions,
            IReadOnlyList<JsonNode> input, JsonArray? tools, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            if (IsDecision(tools)) DecisionCalls++;
            return Task.FromResult(response(Calls, input, tools));
        }
    }
}
