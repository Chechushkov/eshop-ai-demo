using eShop.SupportEvaluation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace eShop.SupportEvaluation.UnitTests;

[TestClass]
public sealed class LoginTests
{
    [TestMethod]
    public void PkceS256MatchesRfcExample() => Assert.AreEqual("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
        BuyerLogin.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [TestMethod]
    public void CallbackChecksStateAndRejectsDuplicateCodes()
    {
        Assert.AreEqual("code-fixture", BuyerLogin.CallbackCode("/callback?code=code-fixture&state=state-fixture", "state-fixture"));
        foreach (var path in new[] { "/callback?code=x&state=other", "/callback?code=x&code=y&state=state-fixture",
            "/callback?code=x&state=%D1%8F" })
            Assert.ThrowsExactly<EvaluationException>(() => BuyerLogin.CallbackCode(path, "state-fixture"));
    }

    [TestMethod]
    public void DiscoveryCannotSendVerifierToAnotherOrigin() => Assert.ThrowsExactly<EvaluationException>(() =>
        LocalEndpoints.DiscoveryEndpoint("https://example.com/connect/token", new Uri("http://localhost:5223")));

    [TestMethod]
    public void ValidStateDenialFinishesLogin() => Assert.ThrowsExactly<AuthorizationDeniedException>(() =>
        BuyerLogin.CallbackCode("/callback?error=access_denied&state=state-fixture", "state-fixture"));

    [TestMethod]
    public void TokenFileHasPrivateUnixPermissions()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "token.json");
        Artifacts.PrivateJson(path, new { access_token = "fixture-not-a-real-token" });
        if (!OperatingSystem.IsWindows())
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
