using System.Text.Json.Nodes;

namespace eShop.SupportApi;

public sealed class SupportAgent(
    IResponsesModel model,
    ReplyComposer composer)
{
    public async Task<SupportReply> RunAsync(
        RunContext context,
        SupportTools tools,
        CancellationToken ct)
    {
        context.Log("agent start");
        context.LogRequest();

        // Place previous messages before the current question.
        var input = ConversationMemory.Messages(context.History);

        input.Add(OpenAiApi.UserMessage(
            $"Выбранный заказ: " +
            $"{context.Request.OrderId?.ToString() ?? "не выбран"}.\n" +
            $"Разрешено создание заявки: " +
            $"{context.Request.AllowTicket}.\n" +
            $"Вопрос: {context.Request.Question}"));

        string text = "";
        bool completed = false;

        // Limit model calls
        // in the tool-selection loop.
        for (int step = 0; step < 6; step++)
        {
            context.Log($"agent model step={step + 1}");

            var response = await model.RespondAsync(
                ReplyComposer.BuildInstructions(context),
                input,
                SupportTools.Definitions(),
                ct);

            var output = response["output"]!.AsArray();

            var calls = output
                .Where(x =>
                    x?["type"]?.GetValue<string>() == "function_call")
                .ToArray();

            // Keep response items for subsequent steps
            // in this run, including reasoning items.
            // Only the final SupportReply is saved in conversation history.
            foreach (var item in output)
                input.Add(item!.DeepClone());

            if (calls.Length == 0)
            {
                text = OpenAiApi.OutputText(response);
                completed = true;
                break;
            }

            foreach (var call in calls)
            {
                string name = call!["name"]!.GetValue<string>();

                context.Log($"agent model requested {name}");

                string result = await tools.InvokeAsync(
                    name,
                    call["arguments"]!.GetValue<string>(),
                    ct);

                // Return the result of the actual C# tool
                // to the model.
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = call["call_id"]!.GetValue<string>(),
                    ["output"] = result
                });
            }
        }

        // Enforce application requirements even
        // when the model skips a required tool.
        bool factsAdded = false;

        if (!context.KnowledgeSearchCompleted)
        {
            context.Log(
                "agent C# invokes SearchKnowledge: " +
                "model did not complete knowledge search");

            string query =
                await composer.ResolveRetrievalQueryAsync(context, ct);

            await tools.SearchKnowledgeAsync(query, ct);
            context.AutoCompletedActions.Add("SearchKnowledge");

            factsAdded = true;
        }

        // Read the selected order status during the current run.
        if (context.Request.OrderId is not null &&
            context.Order is null)
        {
            context.Log(
                "agent C# invokes GetOrderStatus: " +
                "model did not read selected order");

            await tools.GetOrderStatusAsync(ct);
            context.AutoCompletedActions.Add("GetOrderStatus");

            factsAdded = true;
        }

        // Ticket creation is a model-selected action, not a mandatory completion.
        // SupportTools still checks current permission, ownership and idempotency.

        var errors = ReplyComposer.Validate(text, context);

        context.LogValidation(
            "agent validate initial",
            errors);

        if (!completed || factsAdded || errors.Length > 0 || context.HasNoRelevantKnowledge)
        {
            context.ReplyRecomposed = true;
            context.Log("agent repair");

            context.Log(
                $"agent repair reason: completed={completed} " +
                $"factsAdded={factsAdded} errors={errors.Length} " +
                $"knowledgeMissing={context.HasNoRelevantKnowledge}");

            // Recompose the reply using current facts,
            // conversation history and validation feedback.
            text = await composer.ComposeAsync(
                context,
                errors,
                ct);

            errors = ReplyComposer.Validate(text, context);

            context.LogValidation(
                "agent validate repaired",
                errors);
        }

        context.Log(
            errors.Length == 0
                ? "agent finish"
                : "agent fallback");

        return ReplyComposer.Finish(
            text,
            context,
            errors.Length > 0);
    }
}
