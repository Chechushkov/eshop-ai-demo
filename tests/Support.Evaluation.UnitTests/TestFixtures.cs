using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using eShop.SupportEvaluation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace eShop.SupportEvaluation.UnitTests;

internal static class TestData
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "TestData");
    public static PriceTable Prices => PriceTable.Load(Path.Combine(Root, "eval/prices.json"));
    public static OrderFixtures Fixtures => new(1, 2);
    public static IReadOnlyList<Scenario> Scenarios => ScenarioContract.Load(Path.Combine(Root, "eval/scenarios.jsonl"),
        ScenarioContract.DocumentIds(Path.Combine(Root, "src/Support.API/Knowledge")));

    public static JsonObject Metrics() => new()
    {
        ["elapsedMs"] = 120, ["modelCalls"] = 2, ["embeddingCalls"] = 1,
        ["inputTokens"] = 1000, ["outputTokens"] = 100, ["embeddingTokens"] = 50,
        ["isReplay"] = false, ["model"] = Prices.Model, ["embeddingModel"] = Prices.EmbeddingModel
    };

    public static JsonObject PolicyReply(JsonObject request) => new()
    {
        ["answer"] = "Открытая коробка сама по себе не мешает возврату [support-returns-opened#001].",
        ["sources"] = new JsonArray(new JsonObject { ["id"] = "support-returns-opened#001" }),
        ["order"] = null, ["ticket"] = null, ["trace"] = new JsonArray("tool SearchKnowledge"),
        ["usedFallback"] = false, ["conversationId"] = request["conversationId"]?.DeepClone(),
        ["metrics"] = Metrics(), ["behavior"] = new JsonObject
        { ["autoCompletedActions"] = new JsonArray(), ["replyRecomposed"] = false }
    };
}

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "support-evaluation-" + Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal sealed class TestCredential : IBuyerCredential
{
    public string Value => "offline-buyer-token";
    public void EnsureValid() { }
}

internal sealed class TestApi(Func<JsonObject, ApiResponse>? respond = null) : IEvaluationApi
{
    public IBuyerCredential Credential { get; } = new TestCredential();
    public List<JsonObject> Requests { get; } = [];
    public Task<ApiResponse> RequestAsync(string path, JsonObject? payload = null,
        IReadOnlyList<string>? extraSecrets = null, CancellationToken ct = default)
    {
        Assert.AreEqual("/api/support/ask", path);
        Assert.IsNotNull(payload);
        Requests.Add((JsonObject)payload!.DeepClone());
        return Task.FromResult(respond is null ? new ApiResponse(200, TestData.PolicyReply(payload), null, 150) : respond(payload));
    }
}

internal sealed class OrderListApi(JsonArray response) : IEvaluationApi
{
    public IBuyerCredential Credential { get; } = new TestCredential();
    public List<string> Paths { get; } = [];
    public Task<ApiResponse> RequestAsync(string path, JsonObject? payload = null,
        IReadOnlyList<string>? extraSecrets = null, CancellationToken ct = default)
    {
        Assert.IsNull(payload);
        Paths.Add(path);
        return Task.FromResult(new ApiResponse(200, response.DeepClone(), null, 1));
    }
}

internal sealed class MemoryHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(respond(request));
    }
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
