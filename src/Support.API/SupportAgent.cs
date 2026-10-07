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

        // Предыдущие сообщения идут перед текущим вопросом.
        var input = ConversationMemory.Messages(context.History);

        input.Add(OpenAiApi.UserMessage(
            $"Выбранный заказ: " +
            $"{context.Request.OrderId?.ToString() ?? "не выбран"}.\n" +
            $"Разрешено создание заявки: " +
            $"{context.Request.AllowTicket}.\n" +
            $"Вопрос: {context.Request.Question}"));

        string text = "";
        bool completed = false;

        // Ограничиваем количество обращений к модели
        // в цикле выбора инструментов.
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

            // Сохраняем элементы ответа для следующих шагов
            // текущего запуска, включая reasoning items.
            // В историю PostgreSQL сохраняется итоговый SupportReply.
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

                // Результат настоящего C#-инструмента
                // возвращается модели.
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = call["call_id"]!.GetValue<string>(),
                    ["output"] = result
                });
            }
        }

        // Требования приложения обеспечиваем и тогда,
        // когда модель пропустила необходимый инструмент.
        bool factsAdded = false;

        if (context.Sources.Count == 0)
        {
            context.Log(
                "agent C# invokes SearchKnowledge: " +
                "model supplied no sources");

            string query =
                await composer.ResolveRetrievalQueryAsync(context, ct);

            await tools.SearchKnowledgeAsync(query, ct);

            factsAdded = true;
        }

        // Статус выбранного заказа читаем в текущем запуске.
        if (context.Request.OrderId is not null &&
            context.Order is null)
        {
            context.Log(
                "agent C# invokes GetOrderStatus: " +
                "model did not read selected order");

            await tools.GetOrderStatusAsync(ct);

            factsAdded = true;
        }

        // Используем только разрешение текущего сообщения.
        // Дополнительные проверки владения и requestId
        // выполняются внутри SupportTools.
        if (context.Request.AllowTicket &&
            context.Ticket is null)
        {
            context.Log(
                "agent C# invokes CreateSupportTicket: " +
                "allowed request was not handled");

            string summary = context.Request.Question[
                ..Math.Min(1000, context.Request.Question.Length)];

            await tools.CreateSupportTicketAsync(summary, ct);

            factsAdded = true;
        }

        var errors = ReplyComposer.Validate(text, context);

        context.LogValidation(
            "agent validate initial",
            errors);

        if (!completed || factsAdded || errors.Length > 0)
        {
            context.Log("agent repair");

            context.Log(
                $"agent repair reason: completed={completed} " +
                $"factsAdded={factsAdded} errors={errors.Length}");

            // Пересобираем ответ по актуальным фактам,
            // истории и замечаниям валидатора.
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