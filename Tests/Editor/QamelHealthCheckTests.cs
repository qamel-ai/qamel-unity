using NUnit.Framework;
using QamelCapture;
using QamelCapture.Editor;

namespace QamelCapture.Tests
{
    public class QamelHealthCheckTests
    {
        [Test]
        public void PayloadMatchesCaptureSpec()
        {
            var parsed = TestJson.Parse(QamelHealthCheck.BuildPayload());
            Assert.AreEqual("plugin_health", parsed["kind"]);
            Assert.AreEqual("unity", parsed["engine"]);
            Assert.AreEqual("com.qamel.unity", parsed["plugin"]);
            Assert.AreEqual(QamelSettings.PluginVersion, parsed["plugin_version"]);
            Assert.AreEqual(4, parsed.Count);
        }
    }
}
