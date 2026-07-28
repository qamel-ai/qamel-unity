using System;
using NUnit.Framework;
using QamelCapture;

namespace QamelCapture.Tests
{
    public class PluginDiagnosticsTests
    {
        [Test]
        public void PayloadContainsErrorAndEnvironmentFields()
        {
            Exception caught;
            try
            {
                throw new InvalidOperationException("qamel diag test");
            }
            catch (Exception e)
            {
                caught = e;
            }

            var parsed = TestJson.Parse(PluginDiagnostics.BuildPayload("session123", "update loop", caught));

            Assert.AreEqual("plugin_error", parsed["kind"]);
            Assert.AreEqual("session123", parsed["session_id"]);
            Assert.AreEqual("update loop", parsed["where"]);
            Assert.AreEqual("InvalidOperationException: qamel diag test", parsed["error"]);
            Assert.IsTrue(parsed["stack"].Contains("PayloadContainsErrorAndEnvironmentFields"),
                "stack should contain the throwing method");
            Assert.AreEqual("unity", parsed["engine"]);
            Assert.AreEqual(QamelSettings.PluginVersion, parsed["plugin_version"]);
            foreach (var key in new[]
            {
                "engine_version", "game_name", "platform", "os", "gpu",
                "run_environment", "build_configuration", "installation_id",
                "cpu_architecture", "graphics_api", "system_language",
            })
                Assert.IsTrue(parsed.ContainsKey(key), "payload missing field: " + key);
        }

        [Test]
        public void PayloadSurvivesNullError()
        {
            var parsed = TestJson.Parse(PluginDiagnostics.BuildPayload(null, "initialization", null));
            Assert.AreEqual("", parsed["error"]);
            Assert.AreEqual("", parsed["stack"]);
            Assert.AreEqual("", parsed["session_id"]);
        }
    }
}
