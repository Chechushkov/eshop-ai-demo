using System.Text.Json.Nodes;
using eShop.SupportEvaluation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace eShop.SupportEvaluation.UnitTests;

[TestClass]
public sealed class EvaluationContractTests
{
    private static List<JsonNode> Nodes() => File.ReadAllLines(Path.Combine(TestData.Root, "eval/scenarios.jsonl"))
        .Select(EvaluationJson.Parse).ToList();
    private static void Validate(List<JsonNode> nodes) => ScenarioContract.Validate(nodes,
        ScenarioContract.DocumentIds(Path.Combine(TestData.Root, "src/Support.API/Knowledge")));
    private static JsonNode Case(List<JsonNode> nodes, string id) => nodes.Single(n => Wire.Text(n["id"]) == id);

    [TestMethod]
    public void CommittedDatasetIsValid() => Assert.AreEqual(10, TestData.Scenarios.Count);

    [TestMethod]
    public void MissingCurrentPermissionCannotExpectAWrite()
    {
        var nodes = Nodes();
        Case(nodes, "explicit-ticket-permitted")["allow_ticket"] = false;
        var error = Assert.ThrowsExactly<EvaluationException>(() => Validate(nodes));
        Assert.IsTrue(error.Message.Contains("current permission", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PermissionCannotAuthorizeForeignOrderWrite()
    {
        var nodes = Nodes();
        Case(nodes, "foreign-order-denied")["expected"]!["ticket_created"] = true;
        var error = Assert.ThrowsExactly<EvaluationException>(() => Validate(nodes));
        Assert.IsTrue(error.Message.Contains("owned order", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnknownSourceCannotBecomeGroundTruth()
    {
        var nodes = Nodes();
        Case(nodes, "policy-opened-box")["expected"]!["source_groups"] = new JsonArray(new JsonArray("support-invented"));
        var error = Assert.ThrowsExactly<EvaluationException>(() => Validate(nodes));
        Assert.IsTrue(error.Message.Contains("unknown document IDs", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DuplicateScenarioCannotBiasResults()
    {
        var nodes = Nodes();
        nodes[1] = nodes[0].DeepClone();
        var error = Assert.ThrowsExactly<EvaluationException>(() => Validate(nodes));
        Assert.IsTrue(error.Message.Contains("Duplicate scenario id", StringComparison.Ordinal));
    }

    [TestMethod]
    public void HistoryCaseCannotSilentlyRunWithoutHistory()
    {
        var nodes = Nodes();
        Case(nodes, "refund-follow-up")["setup"] = new JsonArray();
        var error = Assert.ThrowsExactly<EvaluationException>(() => Validate(nodes));
        Assert.IsTrue(error.Message.Contains("requires setup turns", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DuplicateJsonKeyCannotHidePermission()
    {
        Assert.ThrowsExactly<EvaluationException>(() => EvaluationJson.Parse("{\"allow_ticket\":false,\"allow_ticket\":true}"));
        Assert.ThrowsExactly<EvaluationException>(() => EvaluationJson.Parse("{\"expected\":{\"ticket_created\":false,\"ticket_created\":true}}"));
    }

    [TestMethod]
    public void FixturesRequireRealDistinctIntegerIds()
    {
        foreach (string invalid in new[] { "{\"owned_order\":null,\"foreign_order\":2}",
            "{\"owned_order\":true,\"foreign_order\":2}", "{\"owned_order\":1,\"foreign_order\":1}",
            "{\"owned_order\":2147483648,\"foreign_order\":2}" })
            Assert.ThrowsExactly<EvaluationException>(() => ScenarioContract.Fixtures(EvaluationJson.Parse(invalid)));
        Assert.AreEqual(new OrderFixtures(1, 2), ScenarioContract.Fixtures(EvaluationJson.Parse("{\"owned_order\":1,\"foreign_order\":2}")));
    }
}
