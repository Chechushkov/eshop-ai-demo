using System.Diagnostics;
using System.Text.Json.Nodes;

namespace eShop.SupportApi;

// One instance per HTTP request.
// Shared by message processing and OpenAiApi clients.
public sealed class SupportMetricsCollector(OpenAiSettings settings)
{
    private readonly object gate = new();
    private readonly Stopwatch timer = new();

    private bool active;
    private int modelCalls;
    private int embeddingCalls;

    private long? inputTokens = 0;
    private long? outputTokens = 0;
    private long? embeddingTokens = 0;

    public void Start()
    {
        lock (gate)
        {
            modelCalls = 0;
            embeddingCalls = 0;

            inputTokens = 0;
            outputTokens = 0;
            embeddingTokens = 0;

            active = true;
            timer.Restart();
        }
    }

    // Count each POST attempt.
    // Missing usage data means the token count is unknown.
    public void Record(string endpoint, JsonObject? response)
    {
        lock (gate)
        {
            if (!active)
                return;

            var usage = response?["usage"] as JsonObject;

            if (endpoint == "responses")
            {
                modelCalls++;

                inputTokens = Sum(
                    inputTokens,
                    ReadToken(usage, "input_tokens"));

                outputTokens = Sum(
                    outputTokens,
                    ReadToken(usage, "output_tokens"));
            }
            else if (endpoint == "embeddings")
            {
                embeddingCalls++;

                embeddingTokens = Sum(
                    embeddingTokens,
                    ReadToken(usage, "total_tokens"));
            }
        }
    }

    public SupportMetrics Finish(bool isReplay = false)
    {
        lock (gate)
        {
            timer.Stop();
            active = false;

            return new SupportMetrics(
                timer.ElapsedMilliseconds,
                modelCalls,
                embeddingCalls,
                inputTokens,
                outputTokens,
                embeddingTokens,
                isReplay,
                settings.Model,
                settings.EmbeddingModel);
        }
    }

    // Stop timing even when processing fails.
    public void Stop()
    {
        lock (gate)
        {
            timer.Stop();
            active = false;
        }
    }

    public static string Describe(SupportMetrics metrics) =>
        $"metrics elapsedMs={metrics.ElapsedMs} " +
        $"modelCalls={metrics.ModelCalls} " +
        $"embeddingCalls={metrics.EmbeddingCalls} " +
        $"inputTokens={metrics.InputTokens?.ToString() ?? "unknown"} " +
        $"outputTokens={metrics.OutputTokens?.ToString() ?? "unknown"} " +
        $"embeddingTokens={metrics.EmbeddingTokens?.ToString() ?? "unknown"} " +
        $"replay={metrics.IsReplay}";

    private static long? ReadToken(
        JsonObject? usage,
        string name)
    {
        if (usage?[name] is not JsonValue value)
            return null;

        if (value.TryGetValue<long>(out long large) && large >= 0)
            return large;

        if (value.TryGetValue<int>(out int small) && small >= 0)
            return small;

        return null;
    }

    private static long? Sum(long? total, long? value)
    {
        if (total is null ||
            value is null ||
            total.Value > long.MaxValue - value.Value)
        {
            return null;
        }

        return total.Value + value.Value;
    }
}