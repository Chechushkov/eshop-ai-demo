using System.Net;
using System.Text.Json.Nodes;
using eShop.SupportEvaluation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace eShop.SupportEvaluation.UnitTests;

[TestClass]
public sealed class TransportAndCliTests
{
    [TestMethod]
    public async Task HttpUsesBuyerTokenAndRedactsSecretsFromSavedReply()
    {
        using var handler = new MemoryHttpHandler(request =>
        {
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("offline-buyer-token", request.Headers.Authorization?.Parameter);
            Assert.AreEqual(HttpMethod.Post, request.Method);
            return MemoryHttpHandler.Json("{\"answer\":\"offline-buyer-token offline-foreign-token\"}");
        });
        using var api = new EvaluationApi("http://localhost:5260", new TestCredential(), handler);
        var response = await api.RequestAsync("/api/support/ask", new JsonObject(), ["offline-foreign-token"]);
        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual("[REDACTED] [REDACTED]", Wire.Text(response.Reply!["answer"]));
    }

    [TestMethod]
    public async Task ErrorResponseBodyIsNotSaved()
    {
        using var handler = new MemoryHttpHandler(_ => MemoryHttpHandler.Json("{\"secret\":\"offline-buyer-token\"}", HttpStatusCode.Unauthorized));
        using var api = new EvaluationApi("http://localhost:5260", new TestCredential(), handler);
        var response = await api.RequestAsync("/api/support/conversations");
        Assert.AreEqual(401, response.HttpStatus);
        Assert.IsNull(response.Reply);
        Assert.AreEqual("API HTTP 401.", response.Error);
    }

    [TestMethod]
    public void CredentialsCannotBeSentToExternalApi() => Assert.ThrowsExactly<EvaluationException>(() =>
        LocalEndpoints.Validate("https://example.com", rootOnly: true));

    [TestMethod]
    public async Task PlanDoesNotLoadTokensOrMakeHttpRequests()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        int code = await EvaluationCli.RunAsync(["plan", "--root", TestData.Root,
            "--buyer-token-file", "does-not-exist.json", "--support-url", "https://example.com"], output, errors);
        Assert.AreEqual(0, code);
        Assert.IsTrue(output.ToString().Contains("No HTTP requests or model calls were made.", StringComparison.Ordinal));
        Assert.AreEqual("", errors.ToString());
    }

    [TestMethod]
    public async Task UnknownOptionIsRejectedBeforeAnyExecution()
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        int code = await EvaluationCli.RunAsync(["run", "--root", TestData.Root, "--allow-tikcet"], output, errors);
        Assert.AreEqual(1, code);
        Assert.IsTrue(errors.ToString().Contains("Unknown option", StringComparison.Ordinal));
    }
}
