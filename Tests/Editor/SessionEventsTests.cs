using NUnit.Framework;
using QamelCapture;

namespace QamelCapture.Tests
{
    /// <summary>
    /// Locks the JSONL event lines to the schema in the Qamel capture wire format.
    /// If one of these fails after a change, the server-side parser (and the
    /// spec) must be updated in the same change.
    /// </summary>
    public class SessionEventsTests
    {
        [Test]
        public void LogEventMatchesSpec()
        {
            var parsed = TestJson.Parse(SessionEvents.Log(1.25, "error", "boom", "at Foo()"));
            Assert.AreEqual("1.25", parsed["t"]);
            Assert.AreEqual("log", parsed["type"]);
            Assert.AreEqual("error", parsed["level"]);
            Assert.AreEqual("boom", parsed["message"]);
            Assert.AreEqual("at Foo()", parsed["stack"]);
        }

        [Test]
        public void LogEventOmitsEmptyStack()
        {
            var parsed = TestJson.Parse(SessionEvents.Log(0, "info", "hi", null));
            Assert.IsFalse(parsed.ContainsKey("stack"));
        }

        [Test]
        public void ContextEventMatchesSpec()
        {
            var capture = new CaptureHealthSnapshot
            {
                Attempted = 60,
                Kept = 58,
                DropInflight = 1,
                DropEncodeQueue = 1,
                ReadbackErrors = 0,
                EncodeErrors = 0,
            };
            var parsed = TestJson.Parse(SessionEvents.Context(
                10, "Level3", 59.94f, 33.5f, 512, 1f, 8.2f, 7.1f, capture));
            Assert.AreEqual("context", parsed["type"]);
            Assert.AreEqual("Level3", parsed["scene"]);
            Assert.AreEqual("59.9", parsed["fps"]);
            Assert.AreEqual("33.5", parsed["frame_ms_max"]);
            Assert.AreEqual("512", parsed["memory_mb"]);
            Assert.AreEqual("1", parsed["time_scale"]);
            Assert.AreEqual("60", parsed["capture_attempted"]);
            Assert.AreEqual("58", parsed["capture_kept"]);
            Assert.AreEqual("1", parsed["capture_drop_inflight"]);
            Assert.AreEqual("1", parsed["capture_drop_encode"]);
            Assert.AreEqual("0", parsed["capture_readback_errors"]);
            Assert.AreEqual("0", parsed["capture_encode_errors"]);
            Assert.AreEqual("8.2", parsed["cpu_frame_ms"]);
            Assert.AreEqual("7.1", parsed["gpu_frame_ms"]);
        }

        [Test]
        public void ContextEventOmitsUnavailableFrameTimings()
        {
            var parsed = TestJson.Parse(SessionEvents.Context(
                1, "Boot", 30f, 40f, 100, 1f, -1f, -1f, default(CaptureHealthSnapshot)));
            Assert.IsFalse(parsed.ContainsKey("cpu_frame_ms"));
            Assert.IsFalse(parsed.ContainsKey("gpu_frame_ms"));
            Assert.AreEqual("0", parsed["capture_attempted"]);
        }

        [Test]
        public void InputEventMatchesSpec()
        {
            var parsed = TestJson.Parse(SessionEvents.Input(2.5, "key_down", "Space"));
            Assert.AreEqual("input", parsed["type"]);
            Assert.AreEqual("key_down", parsed["action"]);
            Assert.AreEqual("Space", parsed["key"]);
        }

        [Test]
        public void MousePosEventMatchesSpec()
        {
            var parsed = TestJson.Parse(SessionEvents.MousePos(2.5, 0.5f, 0.25f));
            Assert.AreEqual("input", parsed["type"]);
            Assert.AreEqual("mouse_pos", parsed["action"]);
            Assert.AreEqual("0.5", parsed["x"]);
            Assert.AreEqual("0.25", parsed["y"]);
        }

        [Test]
        public void CustomEventMatchesSpecAndOmitsNullData()
        {
            var withData = TestJson.Parse(SessionEvents.Custom(1, "level_loaded", "level_3"));
            Assert.AreEqual("custom", withData["type"]);
            Assert.AreEqual("level_loaded", withData["name"]);
            Assert.AreEqual("level_3", withData["data"]);

            var withoutData = TestJson.Parse(SessionEvents.Custom(1, "checkpoint", null));
            Assert.IsFalse(withoutData.ContainsKey("data"));
        }

        [Test]
        public void ReportEventMatchesSpec()
        {
            var parsed = TestJson.Parse(SessionEvents.Report(30, "abc123", "fell through floor"));
            Assert.AreEqual("report", parsed["type"]);
            Assert.AreEqual("abc123", parsed["report_id"]);
            Assert.AreEqual("fell through floor", parsed["text"]);
        }

        [Test]
        public void IdentityEventMatchesSpec()
        {
            var parsed = TestJson.Parse(SessionEvents.Identity(0.25, "set", new IdentitySnapshot
            {
                InstallationId = "0123456789abcdef0123456789abcdef",
                ExternalPlayerId = "account_42",
                ParticipantKind = "playtester",
            }));

            Assert.AreEqual("identity", parsed["type"]);
            Assert.AreEqual("set", parsed["action"]);
            Assert.AreEqual("0123456789abcdef0123456789abcdef", parsed["installation_id"]);
            Assert.AreEqual("account_42", parsed["external_player_id"]);
            Assert.AreEqual("playtester", parsed["participant_kind"]);
        }
    }
}
