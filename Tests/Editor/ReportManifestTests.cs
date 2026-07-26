using System;
using NUnit.Framework;
using QamelCapture;

namespace QamelCapture.Tests
{
    /// <summary>
    /// Locks manifest.json to the field list in the Qamel capture wire format. Runs in the
    /// editor, so the Unity-sourced fields (engine version, OS, GPU...) are real
    /// values; we assert presence and the deterministic fields.
    /// </summary>
    public class ReportManifestTests
    {
        [Test]
        public void ManifestContainsAllSpecFields()
        {
            var started = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);
            string json = ReportManifest.Build(new ReportManifestData
            {
                SessionId = "abc123session",
                ReportId = "report000001",
                ReportT = 93.5,
                UserText = "fell through the floor",
                EventCount = 240,
                FrameCount = 720,
                FrameWidth = 640,
                FrameHeight = 360,
                SessionStartedUtc = started,
                BufferSeconds = 120,
                CaptureFps = 6f,
            });

            var parsed = TestJson.Parse(json);

            Assert.AreEqual("1", parsed["schema"]);
            Assert.AreEqual("report", parsed["kind"]);
            Assert.AreEqual("abc123session", parsed["session_id"]);
            Assert.AreEqual("report000001", parsed["report_id"]);
            Assert.AreEqual("unity", parsed["engine"]);
            Assert.AreEqual("com.qamel.unity", parsed["plugin"]);
            Assert.AreEqual(QamelSettings.PluginVersion, parsed["plugin_version"]);
            Assert.AreEqual("2026-07-22T12:00:00.0000000Z", parsed["session_started_utc"]);
            Assert.AreEqual("93.5", parsed["report_t"]);
            Assert.AreEqual("120", parsed["buffer_seconds"]);
            Assert.AreEqual("6", parsed["capture_fps"]);
            Assert.AreEqual("640", parsed["frame_width"]);
            Assert.AreEqual("360", parsed["frame_height"]);
            Assert.AreEqual("240", parsed["event_count"]);
            Assert.AreEqual("720", parsed["frame_count"]);
            Assert.AreEqual("fell through the floor", parsed["user_text"]);

            // Environment-dependent fields must exist (values vary by machine).
            foreach (var key in new[]
            {
                "engine_version", "game_name", "game_version", "platform", "os",
                "device_id", "device_model", "gpu", "system_memory_mb",
                "screen_width", "screen_height",
            })
            {
                Assert.IsTrue(parsed.ContainsKey(key), "manifest missing field: " + key);
            }
        }

        [Test]
        public void NullUserTextBecomesEmptyString()
        {
            string json = ReportManifest.Build(new ReportManifestData
            {
                SessionId = "s",
                ReportId = "r",
                SessionStartedUtc = DateTime.UtcNow,
                UserText = null,
            });
            Assert.AreEqual("", TestJson.Parse(json)["user_text"]);
        }
    }
}
