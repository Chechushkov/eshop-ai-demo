using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace eShop.SupportApi;

public sealed class OpenAiApi(
    HttpClient http,
    OpenAiSettings settings,
    SupportMetricsCollector? metrics = null) : IResponsesModel
{
    public async Task<JsonObject> RespondAsync(
        string instructions,
        IReadOnlyList<JsonNode> input,
        JsonArray? tools,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["instructions"] = instructions,
            ["input"] = new JsonArray(
                input.Select(x => x.DeepClone()).ToArray()),
            ["store"] = false,
            ["max_output_tokens"] = 2400,
            ["include"] = new JsonArray(
                "reasoning.encrypted_content")
        };

        if (settings.Model.StartsWith(
                "gpt-5", StringComparison.Ordinal) ||
            settings.Model.StartsWith(
                "gpt-6", StringComparison.Ordinal))
        {
            body["reasoning"] = new JsonObject
            {
                ["effort"] = "low"
            };
        }

        if (tools is not null)
        {
            body["tools"] = tools.DeepClone();
            body["parallel_tool_calls"] = false;
            body["tool_choice"] = "auto";
        }

        var response = await PostAsync("responses", body, ct);

        if (response["status"]?.GetValue<string>() != "completed")
        {
            throw new UpstreamException(
                "OpenAI не завершил ответ. " +
                "Проверь лимит выходных токенов в OpenAiApi.cs.");
        }

        return response;
    }

    public async Task<float[][]> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct)
    {
        if (texts.Count == 0)
            return [];

        var body = new JsonObject
        {
            ["model"] = settings.EmbeddingModel,
            ["input"] = new JsonArray(
                texts.Select(x =>
                    (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["dimensions"] = OpenAiSettings.Dimensions,
            ["encoding_format"] = "float"
        };

        var response = await PostAsync("embeddings", body, ct);

        var items = response["data"]?.AsArray()
            ?? throw new UpstreamException(
                "В ответе embeddings нет data.");

        if (items.Count != texts.Count)
        {
            throw new UpstreamException(
                "Количество embeddings не совпадает с input.");
        }

        var result = new float[texts.Count][];

        foreach (var item in items)
        {
            int index = item!["index"]!.GetValue<int>();

            if (index < 0 ||
                index >= result.Length ||
                result[index] is not null)
            {
                throw new UpstreamException(
                    "Некорректные индексы embeddings.");
            }

            var vector = item["embedding"]!
                .AsArray()
                .Select(x => x!.GetValue<float>())
                .ToArray();

            if (vector.Length != OpenAiSettings.Dimensions ||
                vector.Any(x => !float.IsFinite(x)) ||
                vector.All(x => x == 0))
            {
                throw new UpstreamException(
                    "Некорректная размерность или значения embedding.");
            }

            result[index] = vector;
        }

        if (result.Any(x => x is null))
        {
            throw new UpstreamException(
                "Не все embeddings получены.");
        }

        return result;
    }

    private async Task<JsonObject> PostAsync(
        string path,
        JsonObject body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new UpstreamException(
                "API-ключ не настроен: выполни 02-configure-openai.sh.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            path);

        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                settings.ApiKey);

        request.Content = JsonContent.Create(body);

        ct.ThrowIfCancellationRequested();

        JsonObject? result = null;

        try
        {
            using var response = await http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Тело ошибки может содержать пользовательский текст.
                throw new UpstreamException(
                    $"OpenAI HTTP {(int)response.StatusCode}. " +
                    "Проверь ключ, доступ модели, баланс и лимиты.");
            }

            result = await response.Content
                .ReadFromJsonAsync<JsonObject>(
                    cancellationToken: ct)
                ?? throw new UpstreamException(
                    "Пустой ответ OpenAI.");

            return result;
        }
        finally
        {
            // Учитываем попытку даже при исключении.
            // Если ответ не получен, расход токенов неизвестен.
            metrics?.Record(path, result);
        }
    }

    public static string OutputText(JsonObject response) =>
        string.Join(
            "\n",
            response["output"]!
                .AsArray()
                .Where(x =>
                    x?["type"]?.GetValue<string>() == "message")
                .SelectMany(x => x!["content"]!.AsArray())
                .Where(x =>
                    x?["type"]?.GetValue<string>() == "output_text")
                .Select(x => x!["text"]!.GetValue<string>()));

    public static JsonObject UserMessage(string text) => new()
    {
        ["role"] = "user",
        ["content"] = text
    };
}