using System;
using NUnit.Framework;
using QamelCapture.Editor;

namespace QamelCapture.Tests
{
    public sealed class QamelSettingsLinkTests
    {
        [TestCase("https://ingest.qamel.ai", "https://qamel.ai/play-keys")]
        [TestCase("https://custom.example", "https://qamel.ai/play-keys")]
        [TestCase(null, "https://qamel.ai/play-keys")]
        [TestCase("http://localhost:3000", "http://localhost:3000/play-keys")]
        [TestCase("http://127.0.0.1:3000/v1?key=discarded", "http://127.0.0.1:3000/play-keys")]
        [TestCase("http://[::1]:3000", "http://[::1]:3000/play-keys")]
        [TestCase("http://localhost.evil.example", "https://qamel.ai/play-keys")]
        [TestCase("https://user:secret@localhost:3000", "https://qamel.ai/play-keys")]
        public void ApiKeyLinksOpenTheAppWithoutSendingAKey(string endpoint, string expected)
        {
            Assert.AreEqual(expected, QamelSettingsLink.ApiKeysUrl(endpoint));
        }

        const string Link = "com.unity.editor://editor/qamelcapture/settings";

        [TestCase("")]
        [TestCase("/")]
        public void AcceptsTheSettingsRoute(string suffix)
        {
            Assert.IsTrue(QamelSettingsLink.IsSettingsLink(new Uri(Link + suffix)));
        }

        [TestCase("?key=not-a-real-key")]
        [TestCase("?endpoint=https://example.com")]
        [TestCase("#fragment")]
        [TestCase("/extra")]
        public void RejectsAdditionalInstructions(string suffix)
        {
            Assert.IsFalse(QamelSettingsLink.IsSettingsLink(new Uri(Link + suffix)));
        }

        [TestCase("https://editor/qamelcapture/settings")]
        [TestCase("com.unity.editor://other/qamelcapture/settings")]
        [TestCase("com.unity.editor://user@editor/qamelcapture/settings")]
        [TestCase("com.unity.editor://editor:42/qamelcapture/settings")]
        [TestCase("com.unity.editor://editor/editor/qamelcapture/settings")]
        [TestCase("com.unity.editor://editor/qamelcapture/prepare")]
        public void RejectsOtherOriginsAndRoutes(string link)
        {
            Assert.IsFalse(QamelSettingsLink.IsSettingsLink(new Uri(link)));
        }

        [Test]
        public void RejectsMissingOrRelativeLinks()
        {
            Assert.IsFalse(QamelSettingsLink.IsSettingsLink(null));
            Assert.IsFalse(QamelSettingsLink.IsSettingsLink(new Uri("settings", UriKind.Relative)));
        }
    }
}
