using NUnit.Framework;
using QamelCapture.Editor;

namespace QamelCapture.Tests
{
    public class QamelUpdateCheckTests
    {
        [Test]
        public void ReadsTheQamelPayload()
        {
            string body =
                "{\"package\":\"com.qamel.unity\",\"latest\":\"0.2.0\"," +
                "\"minSupported\":\"0.1.0\"," +
                "\"gitUrl\":\"https://github.com/qamel-ai/qamel-unity.git\"," +
                "\"notesUrl\":\"https://github.com/qamel-ai/qamel-unity/releases/tag/v0.2.0\"}";

            Assert.IsTrue(QamelUpdateCheck.TryParsePayload(body, out string latest,
                out string minSupported, out string notesUrl));
            Assert.AreEqual("0.2.0", latest);
            Assert.AreEqual("0.1.0", minSupported);
            Assert.AreEqual("https://github.com/qamel-ai/qamel-unity/releases/tag/v0.2.0", notesUrl);
        }

        /// <summary>The fallback reads the package manifest, which says "version".</summary>
        [Test]
        public void ReadsThePackageManifestFallback()
        {
            string body = "{\"name\":\"com.qamel.unity\",\"version\":\"0.3.1\",\"unity\":\"2021.3\"}";

            Assert.IsTrue(QamelUpdateCheck.TryParsePayload(body, out string latest,
                out string minSupported, out string notesUrl));
            Assert.AreEqual("0.3.1", latest);
            Assert.AreEqual("", minSupported);
            Assert.AreEqual("", notesUrl);
        }

        /// <summary>
        /// A captive portal, a proxy error page or a truncated response must leave
        /// the stored version untouched rather than nag about nothing.
        /// </summary>
        [Test]
        public void RejectsAnythingWithoutAVersion()
        {
            foreach (string body in new[]
                     {
                         null,
                         "",
                         "   ",
                         "<html><body>Sign in to the network</body></html>",
                         "{}",
                         "{\"latest\":\"\"}",
                         "{\"latest\":\"unknown\"}",
                         "{\"error\":\"unknown package\"}",
                         "{\"latest\":\"0.2",
                     })
            {
                Assert.IsFalse(
                    QamelUpdateCheck.TryParsePayload(body, out _, out _, out _),
                    body ?? "null");
            }
        }

        [Test]
        public void IgnoresAnUnsupportedMinVersionAndNonHttpsNotes()
        {
            string body = "{\"latest\":\"0.2.0\",\"minSupported\":\"garbage\"," +
                          "\"notesUrl\":\"javascript:alert(1)\"}";

            Assert.IsTrue(QamelUpdateCheck.TryParsePayload(body, out string latest,
                out string minSupported, out string notesUrl));
            Assert.AreEqual("0.2.0", latest);
            Assert.AreEqual("", minSupported, "an unparseable minSupported must be dropped");
            Assert.AreEqual("", notesUrl, "only absolute https URLs may be opened");
        }
    }
}
