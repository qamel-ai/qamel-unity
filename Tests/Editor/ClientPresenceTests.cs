using System;
using System.Text;
using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;

namespace QamelCapture.Tests
{
    public sealed class ClientPresenceTests
    {
        const string Project = "018f0000-0000-7000-8000-000000000001";
        const string Link = "com.unity.editor://editor/qamelcapture/open/" + Project;
        static string Key => "qa_key_" + new string('a', 32);

        [Test]
        public void PresenceDoesNotNeedPlayModeOrATestProvider()
        {
            Assert.IsTrue(ClientPresenceProtocol.CanSend(true, false, false, false, "http://localhost:3000", Key));
            Assert.AreEqual(10, ClientPresenceProtocol.IntervalSeconds);
        }
        [TestCase(false, false, false, false)]
        [TestCase(true, true, false, false)]
        [TestCase(true, false, true, false)]
        [TestCase(true, false, false, true)]
        public void DisabledBatchAndImportingEditorsDoNotSend(bool enabled, bool batch, bool compiling, bool updating)
        {
            Assert.IsFalse(ClientPresenceProtocol.CanSend(enabled, batch, compiling, updating, "http://localhost:3000", Key));
        }
        [Test]
        public void CredentialsAndInsecureNonlocalEndpointsAreRejected()
        {
            Assert.IsFalse(ClientPresenceProtocol.CanSend(true, false, false, false, "http://example.com", Key));
            Assert.IsFalse(ClientPresenceProtocol.CanSend(true, false, false, false, "https://qamel.ai", "invalid"));
        }
        [Test]
        public void PayloadContainsOnlyInstallationAndDisplayName()
        {
            var json = Encoding.UTF8.GetString(ClientPresenceProtocol.Bytes(Project, "FPS project"));
            StringAssert.Contains("\"contractVersion\":1", json);
            StringAssert.Contains("\"installationId\":\"" + Project + "\"", json);
            StringAssert.DoesNotContain("apiKey", json);
            StringAssert.DoesNotContain("projectId", json);
            StringAssert.DoesNotContain("lastSeen", json);
            StringAssert.DoesNotContain("ready", json);
            Assert.LessOrEqual(ClientPresenceProtocol.Bytes(Project, new string('\n', 250)).Length, 2048);
            Assert.Throws<ArgumentException>(() => ClientPresenceProtocol.Bytes(Guid.Empty.ToString(), "Bad"));
        }
        [Test]
        public void OpenLinkContainsOnlyTheExpectedProject()
        {
            Assert.IsTrue(ClientPresenceProtocol.TryReadOpenLink(new Uri(Link), out var project));
            Assert.AreEqual(Project, project);
        }
        [TestCase("?key=secret")]
        [TestCase("?endpoint=https://elsewhere.example")]
        [TestCase("#fragment")]
        [TestCase("/extra")]
        public void ExtraOpenInstructionsAreRejected(string suffix)
        {
            Assert.IsFalse(ClientPresenceProtocol.TryReadOpenLink(new Uri(Link + suffix), out _));
        }
        [Test]
        public void OtherSchemesAndHostsAreRejected()
        {
            Assert.IsFalse(ClientPresenceProtocol.TryReadOpenLink(new Uri(Link.Replace("com.unity.editor", "https")), out _));
            Assert.IsFalse(ClientPresenceProtocol.TryReadOpenLink(new Uri(Link.Replace("://editor/", "://other/")), out _));
            Assert.IsFalse(ClientPresenceProtocol.TryReadOpenLink(null, out _));
        }
    }
}
