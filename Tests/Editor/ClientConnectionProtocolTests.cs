using System;
using System.Text;
using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;
using UnityEngine;

namespace QamelCapture.Tests
{
    public sealed class ClientConnectionProtocolTests
    {
        const string Project = "018f0000-0000-7000-8000-000000000001";
        const string Prefix = "com.unity.editor://editor/qamelcapture/";
        static PendingClientConnection Pending() => new PendingClientConnection {
            projectId = Project, endpoint = ClientConnectionProtocol.LocalOrigin,
            verifier = ClientConnectionProtocol.RandomToken(), state = ClientConnectionProtocol.RandomToken(),
            apiKey = ClientConnectionProtocol.CreateKey(), fingerprint = "original", expiresAt = 1100,
            label = "A project & name",
        };

        [Test]
        public void PkceMatchesRfc7636Vector()
        {
            Assert.AreEqual("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
                ClientConnectionProtocol.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        }

        [Test]
        public void SecretsHaveExpectedShapesAndAreNotInApprovalUrlOrKeyBody()
        {
            var pending = Pending();
            Assert.IsTrue(TestDefinitionRoutes.IsValidProjectApiKey(pending.apiKey));
            StringAssert.IsMatch("^[A-Za-z0-9_-]{43}$", pending.verifier);
            Assert.AreNotEqual(pending.verifier, pending.state);
            Assert.AreNotEqual(pending.apiKey, ClientConnectionProtocol.CreateKey());
            var url = ClientConnectionProtocol.ApprovalUrl(pending);
            StringAssert.DoesNotContain(pending.verifier, url);
            StringAssert.DoesNotContain(pending.apiKey, url);
            StringAssert.Contains("A%20project%20%26%20name", url);
            var body = Encoding.UTF8.GetString(ClientConnectionProtocol.ExchangeBytes(pending));
            StringAssert.DoesNotContain(pending.apiKey, body);
            StringAssert.Contains(ClientConnectionProtocol.Hash(pending.apiKey), body);
            StringAssert.Contains(pending.apiKey.Substring(0, 13), body);
        }

        [TestCase("production", "https://qamel.ai")]
        [TestCase("local", "http://localhost:3000")]
        public void StartLinksUseClosedEndpointChoices(string environment, string expected)
        {
            Assert.IsTrue(ClientConnectionProtocol.TryReadLink(new Uri(Prefix + "connect/" + Project + "/" + environment), out var project, out var endpoint, out _, out _));
            Assert.AreEqual(Project, project);
            Assert.AreEqual(expected, endpoint);
        }

        [TestCase("local?endpoint=https://evil.example")]
        [TestCase("local#fragment")]
        [TestCase("local/extra")]
        [TestCase("preview")]
        public void RejectsAdditionalInstructions(string suffix)
        {
            Assert.IsFalse(ClientConnectionProtocol.TryReadLink(new Uri(Prefix + "connect/" + Project + "/" + suffix), out _, out _, out _, out _));
        }

        [TestCase("https://editor/")]
        [TestCase("com.unity.editor://user@editor/")]
        [TestCase("com.unity.editor://other/")]
        [TestCase("com.unity.editor://editor:42/")]
        public void RejectsOtherCallbackOrigins(string prefix)
        {
            Assert.IsFalse(ClientConnectionProtocol.TryReadLink(new Uri(prefix + "qamelcapture/connect/" + Project + "/local"), out _, out _, out _, out _));
        }

        [Test]
        public void CallbackRequiresUnexpiredMatchingLocalRequestAndUnchangedSettings()
        {
            var pending = Pending();
            var code = ClientConnectionProtocol.RandomToken();
            Assert.IsTrue(ClientConnectionProtocol.TryReadLink(new Uri(Prefix + "connected/" + Project + "/" + pending.state + "/" + code), out var project, out _, out var state, out var parsedCode));
            Assert.IsTrue(ClientConnectionProtocol.MatchesCallback(pending, project, state, parsedCode, 1000, "original"));
            Assert.IsFalse(ClientConnectionProtocol.MatchesCallback(null, project, state, code, 1000, "original"));
            Assert.IsFalse(ClientConnectionProtocol.MatchesCallback(pending, Guid.NewGuid().ToString(), state, code, 1000, "original"));
            Assert.IsFalse(ClientConnectionProtocol.MatchesCallback(pending, project, ClientConnectionProtocol.RandomToken(), code, 1000, "original"));
            Assert.IsFalse(ClientConnectionProtocol.MatchesCallback(pending, project, state, code, 1100, "original"));
            Assert.IsFalse(ClientConnectionProtocol.MatchesCallback(pending, project, state, code, 1000, "changed"));
            pending.code = code;
            Assert.IsTrue(ClientConnectionProtocol.MatchesCallback(pending, project, state, code, 1000, "original"));
            Assert.IsFalse(ClientConnectionProtocol.MatchesCallback(pending, project, state, ClientConnectionProtocol.RandomToken(), 1000, "original"));
        }

        [Test]
        public void PersistedRetryKeepsTheSameProofAndCandidateKey()
        {
            var pending = Pending();
            pending.code = ClientConnectionProtocol.RandomToken();
            var restored = JsonUtility.FromJson<PendingClientConnection>(JsonUtility.ToJson(pending));
            Assert.IsTrue(ClientConnectionProtocol.IsValid(restored, 1000, "original"));
            CollectionAssert.AreEqual(ClientConnectionProtocol.ExchangeBytes(pending), ClientConnectionProtocol.ExchangeBytes(restored));
            restored.endpoint = "https://untrusted.example";
            Assert.IsFalse(ClientConnectionProtocol.IsValid(restored, 1000, "original"));
        }

        [Test]
        public void RejectsMalformedOrWrongProjectAcknowledgements()
        {
            Assert.IsTrue(ClientConnectionProtocol.AcceptsResponse("{\"projectId\":\"" + Project + "\",\"apiKeyId\":\"" + Guid.NewGuid() + "\"}", Project));
            Assert.IsFalse(ClientConnectionProtocol.AcceptsResponse("{}", Project));
            Assert.IsFalse(ClientConnectionProtocol.AcceptsResponse("not JSON", Project));
            Assert.IsFalse(ClientConnectionProtocol.AcceptsResponse("{\"projectId\":\"" + Guid.NewGuid() + "\",\"apiKeyId\":\"" + Guid.NewGuid() + "\"}", Project));
        }
    }
}
