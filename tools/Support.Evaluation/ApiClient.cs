using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace eShop.SupportEvaluation;

public interface IBuyerCredential
{
    string Value { get; }
    void EnsureValid();
}

public sealed class BuyerCredential : IBuyerCredential
{
    public string Value { get; }
    private readonly DateTimeOffset expires;

    public BuyerCredential(string path)
    {
        var node = EvaluationJson.Parse(File.ReadAllText(path));
        string? value = Wire.Text(Wire.Field(node, "access_token"));
        long? seconds = Wire.Integer(Wire.Field(node, "expires_at"));
        EvaluationJson.Require(!string.IsNullOrWhiteSpace(value) && !value.Any(char.IsWhiteSpace), "Token file has no valid access_token.");
        EvaluationJson.Require(seconds is > 0 and <= 253402300799, "Token file has no valid expires_at.");
        Value = value!;
        expires = DateTimeOffset.FromUnixTimeSeconds(seconds!.Value);
    }

    public void EnsureValid()
    {
        if (expires <= DateTimeOffset.UtcNow.AddSeconds(30))
            throw new EvaluationException("The buyer token expired or expires shortly. Run the login helper again.");
    }
}

public sealed record ApiResponse(int? HttpStatus, JsonNode? Reply, string? Error, long? WallMs);

public interface IEvaluationApi
{
    IBuyerCredential Credential { get; }
    Task<ApiResponse> RequestAsync(string path, JsonObject? payload = null,
        IReadOnlyList<string>? extraSecrets = null, CancellationToken ct = default);
}

public static class LocalEndpoints
{
    public static Uri Validate(string url, bool rootOnly = false)
    {
        bool parsed = Uri.TryCreate(url, UriKind.Absolute, out var uri);
        EvaluationJson.Require(parsed && uri is not null && uri.Scheme is "http" or "https" &&
            uri.Host.Trim('[', ']') is "localhost" or "127.0.0.1" or "::1" &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
            (!rootOnly || uri.AbsolutePath == "/"), "Use a loopback HTTP(S) URL without credentials or query parameters.");
        return uri!;
    }

    public static Uri DiscoveryEndpoint(string? value, Uri authority)
    {
        var uri = Validate(value ?? "");
        EvaluationJson.Require(uri.Scheme == authority.Scheme && uri.Host == authority.Host && uri.Port == authority.Port,
            "Discovery endpoints must have the same origin as Identity.API.");
        return uri;
    }
}

public sealed class EvaluationApi : IEvaluationApi, IDisposable
{
    private readonly HttpClient http;
    public IBuyerCredential Credential { get; }

    public EvaluationApi(string url, IBuyerCredential credential, HttpMessageHandler? handler = null)
    {
        Credential = credential;
        http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = LocalEndpoints.Validate(url, rootOnly: true), Timeout = TimeSpan.FromSeconds(260)
        };
    }

    public async Task<ApiResponse> RequestAsync(string path, JsonObject? payload = null,
        IReadOnlyList<string>? extraSecrets = null, CancellationToken ct = default)
    {
        Credential.EnsureValid();
        EvaluationJson.Require(path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal), "Use a local API path.");
        using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Credential.Value);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (payload is not null) request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, ct);
            int status = (int)response.StatusCode;
            if (status != 200) return new(status, null, $"API HTTP {status}.", watch.ElapsedMilliseconds);
            string raw = await response.Content.ReadAsStringAsync(ct);
            foreach (var secret in new[] { Credential.Value }.Concat(extraSecrets ?? []))
                if (!string.IsNullOrEmpty(secret)) raw = raw.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            try { return new(status, EvaluationJson.Parse(raw), null, watch.ElapsedMilliseconds); }
            catch (EvaluationException) { return new(status, null, "API returned invalid JSON.", watch.ElapsedMilliseconds); }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
        {
            return new(null, null, "Transport failed; the server outcome is unknown.", watch.ElapsedMilliseconds);
        }
    }

    public void Dispose() => http.Dispose();
}

public sealed record PreflightResult(JsonNode OwnedOrder, bool ForeignOrderVerified);

public static class OwnershipPreflight
{
    public static async Task<PreflightResult> VerifyAsync(IEvaluationApi support, IEvaluationApi ordering,
        IEvaluationApi otherOrdering, OrderFixtures fixtures, CancellationToken ct = default)
    {
        static async Task<JsonArray> Orders(IEvaluationApi api, CancellationToken token)
        {
            var response = await api.RequestAsync("/api/orders/?api-version=1.0", ct: token);
            EvaluationJson.Require(response.HttpStatus == 200 && response.Reply is JsonArray,
                "Ordering.API failed. Check its endpoint and buyer token.");
            var orders = (JsonArray)response.Reply!;
            EvaluationJson.Require(orders.All(o => Wire.Integer(Wire.Field(o, "orderNumber")) is > 0 and <= int.MaxValue),
                "Ordering.API returned an invalid order list.");
            return orders;
        }
        var owned = await Orders(ordering, ct);
        var other = await Orders(otherOrdering, ct);
        var ownIds = owned.Select(o => Wire.Integer(Wire.Field(o, "orderNumber"))).ToHashSet();
        var otherIds = other.Select(o => Wire.Integer(Wire.Field(o, "orderNumber"))).ToHashSet();
        EvaluationJson.Require(ownIds.Contains(fixtures.OwnedOrder) && !ownIds.Contains(fixtures.ForeignOrder),
            "The main buyer must own owned_order and must not own foreign_order.");
        EvaluationJson.Require(otherIds.Contains(fixtures.ForeignOrder) && !otherIds.Contains(fixtures.OwnedOrder),
            "The other buyer must own foreign_order and must not own owned_order.");
        var accepted = await support.RequestAsync("/api/support/conversations", ct: ct);
        EvaluationJson.Require(accepted.HttpStatus == 200 && accepted.Reply is JsonArray,
            "Support.API rejected the buyer token. Check the endpoint and orders audience.");
        return new(owned.Single(o => Wire.Integer(Wire.Field(o, "orderNumber")) == fixtures.OwnedOrder)!.DeepClone(), true);
    }
}
