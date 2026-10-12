using System;
using NUnit.Framework;
using QamelCapture.Editor;

namespace QamelCapture.Tests
{
    public sealed class QamelSettingsLinkTests
    {
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
