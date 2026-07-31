using System.Collections.Generic;
using NUnit.Framework;
using QamelCapture;
using UnityEngine;

namespace QamelCapture.Tests
{
    public class InputRecorderTests
    {
        static QamelSettings MakeSettings(bool captureInput = true, bool captureMouse = true)
        {
            var settings = ScriptableObject.CreateInstance<QamelSettings>();
            settings.captureInput = captureInput;
            settings.captureMousePosition = captureMouse;
            return settings;
        }

        static List<string> Events(SessionBuffer buffer)
        {
            var events = new List<string>();
            buffer.Snapshot(events, new List<CapturedFrame>());
            return events;
        }

        [Test]
        public void EmitWritesInputEventIntoTheBuffer()
        {
            var settings = MakeSettings();
            var buffer = new SessionBuffer(60, 8);
            var recorder = new InputRecorder(settings, buffer, () => 2.5);

            recorder.Emit("key_down", "Space");
            recorder.Emit("key_up", "Space");
            recorder.Emit("key_down", "mouse_left");

            var events = Events(buffer);
            Assert.AreEqual(3, events.Count);

            var down = TestJson.Parse(events[0]);
            Assert.AreEqual("2.5", down["t"]);
            Assert.AreEqual("input", down["type"]);
            Assert.AreEqual("key_down", down["action"]);
            Assert.AreEqual("Space", down["key"]);

            Assert.AreEqual("key_up", TestJson.Parse(events[1])["action"]);
            Assert.AreEqual("mouse_left", TestJson.Parse(events[2])["key"]);

            Object.DestroyImmediate(settings);
        }

        [Test]
        public void EmitIsANoOpWhenCaptureInputIsDisabled()
        {
            var settings = MakeSettings(captureInput: false);
            var buffer = new SessionBuffer(60, 8);
            var recorder = new InputRecorder(settings, buffer, () => 1);

            recorder.Emit("key_down", "A");
            recorder.Tick();

            Assert.AreEqual(0, Events(buffer).Count);
            Object.DestroyImmediate(settings);
        }
    }
}
