using System;
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

        [Test]
        public void HeartbeatsAreHourlyAndCoalesceStateChangesWhileFailuresBackOff()
        {
            var now = DateTime.UtcNow;
            Assert.IsTrue(QamelHealthCheck.HeartbeatIsDue("new", "old", "", false, now));
            Assert.IsFalse(QamelHealthCheck.HeartbeatIsDue("new", "old", now.AddSeconds(-29).Ticks.ToString(), true, now));
            Assert.IsTrue(QamelHealthCheck.HeartbeatIsDue("new", "old", now.AddSeconds(-30).Ticks.ToString(), true, now));
            Assert.IsFalse(QamelHealthCheck.HeartbeatIsDue("same", "same", now.AddSeconds(-3599).Ticks.ToString(), true, now));
            Assert.IsTrue(QamelHealthCheck.HeartbeatIsDue("same", "same", now.AddHours(-1).Ticks.ToString(), true, now));
            Assert.IsFalse(QamelHealthCheck.HeartbeatIsDue("new", "old", now.AddSeconds(-599).Ticks.ToString(), false, now));
            Assert.IsTrue(QamelHealthCheck.HeartbeatIsDue("new", "old", now.AddMinutes(-10).Ticks.ToString(), false, now));
        }

        [Test]
        public void DisablingDiagnosticsPreservesTheOriginalKeyValidationPayload()
        {
            var settings = UnityEngine.ScriptableObject.CreateInstance<QamelSettings>();
            try
            {
                settings.sendPluginDiagnostics = false;
                settings.apiKey = "must-never-appear-in-payload";
                string payload = QamelHealthCheck.BuildPayload(settings);
                Assert.AreEqual(4, TestJson.Parse(payload).Count);
                StringAssert.DoesNotContain(settings.apiKey, payload);
            }
            finally { UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void EditorIdentityIsStableInMachineLocalPreferences()
        {
            string first = QamelHealthCheck.EditorInstallationId;
            Assert.IsTrue(Guid.TryParseExact(first, "N", out _));
            Assert.AreEqual(first, QamelHealthCheck.EditorInstallationId);
        }
    }
}
