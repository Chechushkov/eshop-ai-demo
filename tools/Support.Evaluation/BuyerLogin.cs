using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace eShop.SupportEvaluation;

public sealed class AuthorizationDeniedException() : EvaluationException("Authorization was denied. Start a new login.");

public static class BuyerLogin
{
    public const string ClientId = "support-eval";
    public const string RedirectUri = "http://127.0.0.1:8765/callback";

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string CallbackCode(string pathAndQuery, string expectedState)
    {
        var uri = new Uri(new Uri(RedirectUri), pathAndQuery);
        EvaluationJson.Require(uri.AbsolutePath == "/callback", "Unexpected callback path.");
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            string key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            string value = Uri.UnescapeDataString((pair.Length > 1 ? pair[1] : "").Replace('+', ' '));
            if (!values.TryGetValue(key, out var list)) values[key] = list = [];
            list.Add(value);
        }
        EvaluationJson.Require(values.TryGetValue("state", out var states) && states.Count == 1 &&
            states[0].All(c => c <= 127) && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(states[0]), Encoding.ASCII.GetBytes(expectedState)), "Authorization state did not match.");
        if (values.ContainsKey("error")) throw new AuthorizationDeniedException();
        EvaluationJson.Require(values.TryGetValue("code", out var codes) && codes.Count == 1 && !string.IsNullOrWhiteSpace(codes[0]),
            "The callback must contain exactly one authorization code.");
        return codes![0];
    }

    public static async Task LoginAsync(string authorityUrl, string tokenFile, bool noBrowser,
        CancellationToken ct = default)
    {
        var authority = LocalEndpoints.Validate(authorityUrl, rootOnly: true);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
        var discovery = await Fetch(http, new Uri(authority, "/.well-known/openid-configuration"), null, ct);
        var authorize = LocalEndpoints.DiscoveryEndpoint(Wire.Text(Wire.Field(discovery, "authorization_endpoint")), authority);
        var tokenEndpoint = LocalEndpoints.DiscoveryEndpoint(Wire.Text(Wire.Field(discovery, "token_endpoint")), authority);
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        string state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = ClientId, ["response_type"] = "code", ["scope"] = "orders",
            ["redirect_uri"] = RedirectUri, ["code_challenge_method"] = "S256",
            ["code_challenge"] = Challenge(verifier), ["state"] = state, ["prompt"] = "login"
        };
        string url = authorize.AbsoluteUri + "?" + string.Join("&", parameters.Select(p =>
            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:8765/");
        try { listener.Start(); }
        catch (HttpListenerException) { throw new EvaluationException("Cannot bind 127.0.0.1:8765. Close the other login helper and retry."); }
        Console.WriteLine("Sign in as the buyer for this token file.");
        Console.WriteLine("Authorization URL: " + url);
        if (!noBrowser)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            { Console.WriteLine("Open the printed authorization URL in your browser."); }
        }
        string? code = null;
        try
        {
            while (code is null)
            {
                var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
                try { code = CallbackCode(context.Request.RawUrl ?? "", state); }
                catch (AuthorizationDeniedException)
                {
                    await Respond(context, 400, "Authorization denied. Return to the login terminal.");
                    throw;
                }
                catch (Exception error) when (error is EvaluationException or UriFormatException)
                {
                    await Respond(context, 400, "Invalid callback. Return to the login terminal.");
                    continue;
                }
                await Respond(context, 200, "Login received. You can close this tab and return to the terminal.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new EvaluationException("Login timed out. Start a new login; do not reuse the old link."); }
        finally { listener.Stop(); }
        var token = await Fetch(http, tokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri, ["code"] = code, ["code_verifier"] = verifier
        }, ct);
        string? accessToken = Wire.Text(Wire.Field(token, "access_token"));
        long? seconds = Wire.Integer(Wire.Field(token, "expires_in"));
        EvaluationJson.Require(!string.IsNullOrWhiteSpace(accessToken) && !accessToken.Any(char.IsWhiteSpace) &&
            string.Equals(Wire.Text(Wire.Field(token, "token_type")), "Bearer", StringComparison.OrdinalIgnoreCase) &&
            seconds is > 0 and <= 86400, "Identity.API did not return a usable bearer token and expiry.");
        Artifacts.PrivateJson(tokenFile, new JsonObject
        {
            ["access_token"] = accessToken, ["token_type"] = "Bearer",
            ["expires_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds!.Value,
            ["scope"] = Wire.Text(Wire.Field(token, "scope")) ?? "orders", ["authority"] = authorityUrl
        });
        Console.WriteLine($"Token saved to {Path.GetFullPath(tokenFile)}. Lifetime: {seconds} seconds.");
        Console.WriteLine("The bearer token was not printed. Use this private file with the evaluation runner.");
    }

    private static async Task<JsonNode> Fetch(HttpClient http, Uri url,
        Dictionary<string, string>? form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.Accept.ParseAdd("application/json");
        if (form is not null) request.Content = new FormUrlEncodedContent(form);
        try
        {
            using var response = await http.SendAsync(request, ct);
            EvaluationJson.Require(response.StatusCode == HttpStatusCode.OK,
                $"Identity.API HTTP {(int)response.StatusCode}. Check the development client registration.");
            var node = EvaluationJson.Parse(await response.Content.ReadAsStringAsync(ct));
            EvaluationJson.Require(node is JsonObject, "Identity.API returned an invalid JSON object.");
            return node;
        }
        catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new EvaluationException("Cannot read Identity.API. Check the tunnel and the HTTP endpoint."); }
    }

    private static async Task Respond(HttpListenerContext context, int status, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        byte[] body = Encoding.UTF8.GetBytes(text);
        context.Response.ContentLength64 = body.Length;
        try { await context.Response.OutputStream.WriteAsync(body); }
        finally { context.Response.Close(); }
    }
}
