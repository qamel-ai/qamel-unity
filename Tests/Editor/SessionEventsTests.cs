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
            var parsed = TestJson.Parse(SessionEvents.Context(10, "Level3", 59.94f, 512, 1f));
            Assert.AreEqual("context", parsed["type"]);
            Assert.AreEqual("Level3", parsed["scene"]);
            Assert.AreEqual("59.9", parsed["fps"]);
            Assert.AreEqual("512", parsed["memory_mb"]);
            Assert.AreEqual("1", parsed["time_scale"]);
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
    }
}
