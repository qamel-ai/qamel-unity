using System;
using System.Collections.Generic;
using NUnit.Framework;
using QamelCapture;

namespace QamelCapture.Tests
{
    /// <summary>
    /// Spec drift guards: required keys from docs/capture-spec.md for each
    /// event type and for report manifests. Failures here mean update the
    /// plugin and the spec (or server parser) in the same change.
    /// </summary>
    public class CaptureSpecContractTests
    {
        static void RequireKeys(Dictionary<string, string> parsed, params string[] keys)
        {
            foreach (var key in keys)
                Assert.IsTrue(parsed.ContainsKey(key), "missing required key '" + key + "' in: " + string.Join(",", parsed.Keys));
        }

        [Test]
        public void EveryEventTypeEmitsSpecRequiredKeys()
        {
            RequireKeys(TestJson.Parse(SessionEvents.Log(1, "info", "hi", "stack")),
                "t", "type", "level", "message", "stack");
            Assert.AreEqual("log", TestJson.Parse(SessionEvents.Log(1, "info", "hi", null))["type"]);

            RequireKeys(TestJson.Parse(SessionEvents.Context(
                    1, "Boot", 60f, 16f, 100, 1f, 1f, 1f, default(CaptureHealthSnapshot))),
                "t", "type", "scene", "fps", "frame_ms_max", "memory_mb", "time_scale",
                "capture_attempted", "capture_kept", "capture_drop_inflight",
                "capture_drop_encode", "capture_readback_errors", "capture_encode_errors");

            RequireKeys(TestJson.Parse(SessionEvents.Input(1, "key_down", "A")),
                "t", "type", "action", "key");
            RequireKeys(TestJson.Parse(SessionEvents.MousePos(1, 0.1f, 0.2f)),
                "t", "type", "action", "x", "y");
            RequireKeys(TestJson.Parse(SessionEvents.Custom(1, "level_loaded", "x")),
                "t", "type", "name", "data");
            RequireKeys(TestJson.Parse(SessionEvents.Report(1, "rid", "text")),
                "t", "type", "report_id", "text");
            RequireKeys(TestJson.Parse(SessionEvents.Identity(1, "set", new IdentitySnapshot
                {
                    InstallationId = "0123456789abcdef0123456789abcdef",
                    ExternalPlayerId = "",
                    ParticipantKind = "developer",
                })),
                "t", "type", "action", "installation_id", "participant_kind");
        }

        [Test]
        public void EventTypeNamesMatchSpecVocabulary()
        {
            Assert.AreEqual("log", TestJson.Parse(SessionEvents.Log(0, "info", "m", null))["type"]);
            Assert.AreEqual("context", TestJson.Parse(SessionEvents.Context(
                0, "S", 1f, 1f, 1, 1f, -1f, -1f, default(CaptureHealthSnapshot)))["type"]);
            Assert.AreEqual("input", TestJson.Parse(SessionEvents.Input(0, "key_down", "A"))["type"]);
            Assert.AreEqual("custom", TestJson.Parse(SessionEvents.Custom(0, "n", null))["type"]);
            Assert.AreEqual("report", TestJson.Parse(SessionEvents.Report(0, "r", ""))["type"]);
            Assert.AreEqual("identity", TestJson.Parse(SessionEvents.Identity(0, "clear", default(IdentitySnapshot)))["type"]);
        }

        [Test]
        public void ReportManifestRequiresSpecCoreAndCaptureHealthKeys()
        {
            string json = ReportManifest.Build(new ReportManifestData
            {
                SessionId = "sess",
                ReportId = "rep",
                ReportT = 1,
                UserText = "",
                EventCount = 1,
                FrameCount = 1,
                FrameWidth = 320,
                FrameHeight = 180,
                SessionStartedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                BufferSeconds = 60,
                CaptureFps = 6f,
                BuildId = "b",
                Identity = new IdentitySnapshot
                {
                    InstallationId = "0123456789abcdef0123456789abcdef",
                    ParticipantKind = "unknown",
                },
            });

            var parsed = TestJson.Parse(json);
            RequireKeys(parsed,
                "schema", "kind", "session_id", "report_id", "session_started_utc",
                "engine", "plugin", "plugin_version", "report_t", "buffer_seconds",
                "capture_fps", "frame_width", "frame_height", "event_count", "frame_count",
                "user_text",
                "capture_attempted", "capture_kept", "capture_drop_inflight",
                "capture_drop_encode", "capture_readback_errors", "capture_encode_errors",
                "installation_id", "participant_kind");
            Assert.AreEqual("1", parsed["schema"]);
            Assert.AreEqual("report", parsed["kind"]);
        }

        [Test]
        public void PluginErrorKindMatchesSpec()
        {
            var parsed = TestJson.Parse(PluginDiagnostics.BuildPayload("s", "where", null));
            Assert.AreEqual("1", parsed["schema"]);
            Assert.AreEqual("plugin_error", parsed["kind"]);
            RequireKeys(parsed, "session_id", "where", "error", "stack", "engine", "plugin_version");
        }

        [Test]
        public void BundleFrameNamesMatchSpecPattern()
        {
            Assert.AreEqual(
                "frames/f_000118_00019660.jpg",
                ReportBundler.FrameEntryName(new CapturedFrame { Index = 118, T = 19.66 }));
            Assert.AreEqual(
                "frames/f_000000_00000000.jpg",
                ReportBundler.FrameEntryName(new CapturedFrame { Index = 0, T = 0 }));
        }
    }
}
