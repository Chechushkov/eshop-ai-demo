using System.Text.Json.Nodes;
using eShop.SupportEvaluation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace eShop.SupportEvaluation.UnitTests;

[TestClass]
public sealed class RunnerTests
{
    private static Scenario Case(string id) => TestData.Scenarios.Single(s => s.Id == id);
    private static Task<RunResult> Execute(IEvaluationApi client, string directory, RunBudget? budget = null, Scenario? scenario = null) =>
        new EvaluationRunner(TextWriter.Null).RunAsync([scenario ?? Case("policy-opened-box")], TestData.Fixtures,
            ["agent", "workflow"], 1, client, TestData.Prices, budget ?? new RunBudget(1m, .10m, 22), directory);
    private static TestApi Modified(Action<JsonObject> modify) => new(request =>
    {
        var reply = TestData.PolicyReply(request);
        modify(reply);
        return new(200, reply, null, 150);
    });

    [TestMethod]
    public void MeasuredCostUsesUncachedRatesAndIncludesEmbeddings() =>
        Assert.AreEqual(.001201m, TestData.Prices.Estimate(TestData.Metrics()));

    [TestMethod]
    public void UnknownUsageReplayAndModelMismatchAreNotZeroCost()
    {
        foreach (var field in new[] { "inputTokens", "isReplay", "model" })
        {
            var metrics = TestData.Metrics();
            metrics[field] = field switch { "inputTokens" => null, "isReplay" => JsonValue.Create(true), _ => JsonValue.Create("other-model") };
            Assert.IsNull(TestData.Prices.Estimate(metrics));
        }
    }

    [TestMethod]
    public async Task BudgetStopsBeforePostWhenHeadroomIsMissing()
    {
        using var temporary = new TemporaryDirectory();
        var api = new TestApi();
        var result = await Execute(api, temporary.Path, new RunBudget(.09m, .10m, 22));
        Assert.AreEqual(0, api.Requests.Count);
        Assert.AreEqual(0, result.Status.AttemptedRequests);
        Assert.IsTrue(result.Status.StopReason!.Contains("headroom", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UnknownUsageStopsAllLaterPaidRequests()
    {
        using var temporary = new TemporaryDirectory();
        var api = Modified(reply => reply["metrics"]!["inputTokens"] = null);
        var result = await Execute(api, temporary.Path);
        Assert.AreEqual(1, api.Requests.Count);
        Assert.IsTrue(result.Status.UsageUnknown);
        Assert.IsNull(result.Status.TotalCostEstimateUsd);
        Assert.IsNull(result.Records[0].CostEstimateUsd);
    }

    [TestMethod]
    public async Task LostResponseIsSavedOnceAndNeverRetried()
    {
        using var temporary = new TemporaryDirectory();
        var api = new TestApi(_ => new(null, null, "Transport failed; the server outcome is unknown.", 260000));
        var result = await Execute(api, temporary.Path);
        Assert.AreEqual(1, api.Requests.Count);
        Assert.AreEqual(1, result.Records.Count);
        Assert.IsTrue(result.Status.UsageUnknown);
        Assert.IsTrue(File.ReadAllText(Path.Combine(temporary.Path, "requests.jsonl"))
            .Contains(Wire.Text(api.Requests[0]["requestId"])!, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CompletedRequestCanExceedHeadroomAndStopsNextRequest()
    {
        using var temporary = new TemporaryDirectory();
        var api = Modified(reply => reply["metrics"]!["inputTokens"] = 1_000_000);
        var result = await Execute(api, temporary.Path, new RunBudget(.10m, .10m, 22));
        Assert.AreEqual(1, api.Requests.Count);
        Assert.IsTrue(result.Status.KnownCostEstimateUsd > .10m);
        Assert.IsTrue(result.Status.StopReason!.Contains("exceeded", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RequestCapCountsEachAttemptIncludingModes()
    {
        using var temporary = new TemporaryDirectory();
        var api = new TestApi();
        var result = await Execute(api, temporary.Path, new RunBudget(1m, .10m, 1));
        Assert.AreEqual(1, api.Requests.Count);
        Assert.AreEqual(1, result.Status.AttemptedRequests);
        Assert.IsTrue(result.Status.StopReason!.Contains("request limit", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task EachModeUsesFreshConversationAndRequestIds()
    {
        using var temporary = new TemporaryDirectory();
        var api = new TestApi();
        var result = await Execute(api, temporary.Path);
        Assert.IsNull(result.Status.StopReason);
        Assert.AreEqual(2, api.Requests.Count);
        Assert.AreNotEqual(Wire.Text(api.Requests[0]["requestId"]), Wire.Text(api.Requests[1]["requestId"]));
        Assert.AreNotEqual(Wire.Text(api.Requests[0]["conversationId"]), Wire.Text(api.Requests[1]["conversationId"]));
        Assert.IsTrue(api.Requests.All(request => Wire.Boolean(request["demoRetry"]) == false));
    }

    [TestMethod]
    public async Task HistoryIsSentAsSetupInSameConversation()
    {
        using var temporary = new TemporaryDirectory();
        var api = new TestApi();
        var result = await Execute(api, temporary.Path, scenario: Case("refund-follow-up"));
        Assert.AreEqual(4, result.Status.AttemptedRequests);
        CollectionAssert.AreEqual(new[] { "setup", "target", "setup", "target" }, result.Records.Select(r => r.Kind).ToArray());
        Assert.AreEqual(Wire.Text(api.Requests[0]["conversationId"]), Wire.Text(api.Requests[1]["conversationId"]));
        Assert.AreNotEqual(Wire.Text(api.Requests[0]["requestId"]), Wire.Text(api.Requests[1]["requestId"]));
        Assert.IsFalse(api.Requests[1].ContainsKey("history"));
    }

    [TestMethod]
    public async Task FailedSetupIsNotEvaluatedAsValidFollowUp()
    {
        using var temporary = new TemporaryDirectory();
        var api = Modified(reply => reply["ticket"] = new JsonObject
        { ["created"] = true, ["ticket"] = new JsonObject { ["id"] = "T-" + new string('a', 32), ["orderId"] = 1 } });
        var result = await Execute(api, temporary.Path, scenario: Case("refund-follow-up"));
        Assert.AreEqual(2, api.Requests.Count);
        CollectionAssert.AreEqual(new[] { "setup", "skipped", "setup", "skipped" }, result.Records.Select(r => r.Kind).ToArray());
    }

    [TestMethod]
    public void DeniedToolAttemptIsNotCountedAsSavedTicket()
    {
        var scenario = Case("explicit-ticket-not-permitted");
        var request = EvaluationRunner.Payload(scenario.Target, TestData.Fixtures, "agent", Guid.NewGuid());
        var reply = TestData.PolicyReply(request);
        reply["answer"] = "Нужно разрешение [support-support-ticket#001].";
        reply["sources"] = new JsonArray(new JsonObject { ["id"] = "support-support-ticket#001" });
        reply["order"] = new JsonObject { ["found"] = true, ["order"] = new JsonObject { ["orderNumber"] = 1, ["status"] = "Paid" } };
        reply["ticket"] = new JsonObject { ["created"] = false, ["ticket"] = null };
        reply["trace"] = new JsonArray("tool SearchKnowledge", "tool GetOrderStatus", "tool CreateSupportTicket");
        Assert.AreEqual(0, ReplyChecks.Check(reply, scenario, 1).Errors.Length);
    }

    [TestMethod]
    public void RetrievedButUncitedDocumentDoesNotSatisfySourceGroup()
    {
        var reply = TestData.PolicyReply(new JsonObject());
        reply["answer"] = "Можно вернуть при соблюдении правил.";
        Assert.IsTrue(ReplyChecks.Check(reply, Case("policy-opened-box"), null).Errors.Any(e => e.Contains("citation group", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void InventedCitationIsRejected()
    {
        var reply = TestData.PolicyReply(new JsonObject());
        reply["answer"] = Wire.Text(reply["answer"]) + " [invented#001]";
        Assert.IsTrue(ReplyChecks.Check(reply, Case("policy-opened-box"), null).Errors.Any(e => e.Contains("absent from current sources", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void MissingOrMalformedBehaviorRemainsUnknown()
    {
        Assert.IsNull(ReplyChecks.Behavior(new JsonObject()));
        Assert.IsNull(ReplyChecks.Behavior(new JsonObject { ["behavior"] = new JsonObject
        { ["autoCompletedActions"] = new JsonArray(new JsonObject()), ["replyRecomposed"] = false } }));
    }

    [TestMethod]
    public async Task PreflightRejectsArbitraryNonexistentForeignOrder()
    {
        var support = new OrderListApi(new JsonArray());
        var own = new OrderListApi(new JsonArray(new JsonObject { ["orderNumber"] = 1, ["status"] = "Paid" }));
        await Assert.ThrowsExactlyAsync<EvaluationException>(() => OwnershipPreflight.VerifyAsync(support, own,
            new OrderListApi(new JsonArray()), TestData.Fixtures));
    }

    [TestMethod]
    public async Task PreflightVerifiesBothExistingOrdersWithoutModelCalls()
    {
        var own = new OrderListApi(new JsonArray(new JsonObject { ["orderNumber"] = 1, ["status"] = "Paid" }));
        var other = new OrderListApi(new JsonArray(new JsonObject { ["orderNumber"] = 2, ["status"] = "Shipped" }));
        var result = await OwnershipPreflight.VerifyAsync(new OrderListApi(new JsonArray()), own, other, TestData.Fixtures);
        Assert.IsTrue(result.ForeignOrderVerified);
        CollectionAssert.AreEqual(new[] { "/api/orders/?api-version=1.0" }, own.Paths);
        CollectionAssert.AreEqual(new[] { "/api/orders/?api-version=1.0" }, other.Paths);
    }

    [TestMethod]
    public async Task AutomaticPassDoesNotEstablishSemanticSuccess()
    {
        using var temporary = new TemporaryDirectory();
        var result = await Execute(new TestApi(), temporary.Path);
        var record = result.Records[0];
        Assert.AreEqual(0, record.Checks!.Errors.Length);
        Assert.AreEqual("pending_review", EvaluationReport.Outcome(record, null));
        var review = new ManualReview(Wire.Text(record.Request!["requestId"])!, true, true, "Verified.");
        Assert.AreEqual("solved", EvaluationReport.Outcome(record, review));
        record.Reply!["usedFallback"] = true;
        Assert.AreEqual("solved_via_fallback", EvaluationReport.Outcome(record, review));
    }

    [TestMethod]
    public async Task MissingRowVerificationAndSemanticFailureAreDistinct()
    {
        using var temporary = new TemporaryDirectory();
        var result = await Execute(new TestApi(), temporary.Path);
        var record = result.Records[0];
        string id = Wire.Text(record.Request!["requestId"])!;
        Assert.AreEqual("pending_review", EvaluationReport.Outcome(record, new(id, true, null, "")));
        Assert.AreEqual("unsolved", EvaluationReport.Outcome(record, new(id, false, true, "")));
    }

    [TestMethod]
    public async Task SummaryIncludesSetupCostAndDoesNotInventBehavior()
    {
        using var temporary = new TemporaryDirectory();
        await Execute(Modified(reply => reply.Remove("behavior")), temporary.Path, scenario: Case("refund-follow-up"));
        Artifacts.Json(Path.Combine(temporary.Path, "manifest.json"), new JsonObject
        { ["run_id"] = "offline-fixture", ["commit"] = "fixture", ["working_tree_dirty"] = false,
          ["modes"] = new JsonArray("agent", "workflow"), ["planned_requests"] = 4 });
        var summary = EvaluationReport.Save(temporary.Path);
        Assert.AreEqual(1, summary.Modes["agent"].BehaviorUnknown);
        Assert.AreEqual(.004804m, summary.TotalCostEstimateUsd);
        Assert.AreEqual(2, summary.SetupRequests);
        Assert.AreEqual(.002402m, summary.SetupCostEstimateUsd);
    }

    [TestMethod]
    public async Task ReadOnlyTicketSqlCoversDeniedRequestsToo()
    {
        using var temporary = new TemporaryDirectory();
        var result = await Execute(new TestApi(), temporary.Path);
        string sql = File.ReadAllText(Path.Combine(temporary.Path, "verify_tickets.sql"));
        Assert.IsTrue(sql.Contains(Wire.Text(result.Records[0].Request!["requestId"])!, StringComparison.Ordinal));
        Assert.IsTrue(sql.Contains("false", StringComparison.Ordinal));
        Assert.IsFalse(sql.Contains("INSERT", StringComparison.Ordinal));
        Assert.IsFalse(sql.Contains("DELETE", StringComparison.Ordinal));
    }
}
