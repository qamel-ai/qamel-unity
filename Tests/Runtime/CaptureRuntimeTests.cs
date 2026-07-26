using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using NUnit.Framework;
using QamelCapture;
using UnityEngine;
using UnityEngine.TestTools;

namespace QamelCapture.Tests
{
    /// <summary>
    /// PlayMode tests exercising the real capture path: log hook, ambient context,
    /// frame recording via AsyncGPUReadback, and an end-to-end report bundle from
    /// live recorders. Run via Window > General > Test Runner > PlayMode.
    /// </summary>
    public class CaptureRuntimeTests
    {
        sealed class CoroutineHost : MonoBehaviour { }

        static List<string> Events(SessionBuffer buffer)
        {
            var events = new List<string>();
            buffer.Snapshot(events, new List<CapturedFrame>());
            return events;
        }

        static QamelSettings MakeSettings()
        {
            var settings = ScriptableObject.CreateInstance<QamelSettings>();
            settings.captureFps = 10f;
            settings.frameWidth = 320;
            settings.bufferSeconds = 60;
            return settings;
        }

        [UnityTest]
        public IEnumerator LogRecorderCapturesConsoleOutputAndContext()
        {
            var buffer = new SessionBuffer(60, 8);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using (var recorder = new LogRecorder(buffer, () => clock.Elapsed.TotalSeconds))
            {
                Debug.Log("qamel-test-log-line");
                LogAssert.Expect(LogType.Warning, "qamel-test-warning-line");
                Debug.LogWarning("qamel-test-warning-line");
                recorder.Tick(); // emits the first 1 Hz context sample
                yield return null;
            }

            var events = Events(buffer);
            Assert.IsTrue(events.Exists(e => e.Contains("\"type\":\"log\"") &&
                                             e.Contains("\"level\":\"info\"") &&
                                             e.Contains("qamel-test-log-line")), "info log missing");
            Assert.IsTrue(events.Exists(e => e.Contains("\"level\":\"warning\"") &&
                                             e.Contains("qamel-test-warning-line")), "warning missing");
            Assert.IsTrue(events.Exists(e => e.Contains("\"type\":\"context\"")), "context sample missing");
        }

        [UnityTest]
        public IEnumerator LogRecorderInvokesExceptionCallbackOnlyForExceptions()
        {
            var buffer = new SessionBuffer(60, 8);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int exceptionCallbacks = 0;
            using (new LogRecorder(buffer, () => clock.Elapsed.TotalSeconds,
                       () => System.Threading.Interlocked.Increment(ref exceptionCallbacks)))
            {
                Debug.Log("not an exception");
                LogAssert.Expect(LogType.Warning, "also not an exception");
                Debug.LogWarning("also not an exception");
                Assert.AreEqual(0, exceptionCallbacks, "non-exception logs must not trigger the callback");

                LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("qamel-test-exception"));
                Debug.LogException(new System.InvalidOperationException("qamel-test-exception"));
                yield return null;
            }

            Assert.AreEqual(1, exceptionCallbacks, "exception must trigger the callback exactly once");
            var events = Events(buffer);
            Assert.IsTrue(events.Exists(e => e.Contains("\"level\":\"exception\"") &&
                                             e.Contains("qamel-test-exception")), "exception log missing from buffer");
        }

        [UnityTest]
        public IEnumerator LogRecorderFiltersQamelsOwnOutput()
        {
            var buffer = new SessionBuffer(60, 8);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using (var recorder = new LogRecorder(buffer, () => clock.Elapsed.TotalSeconds))
            {
                LogAssert.Expect(LogType.Warning, QLog.Prefix + "internal test warning");
                QLog.Warn("internal test warning");
                yield return null;
            }

            Assert.IsFalse(Events(buffer).Exists(e => e.Contains("internal test warning")),
                "Qamel's own log output must not be captured");
        }

        [UnityTest]
        public IEnumerator FrameRecorderProducesJpegFrames()
        {
            if (Application.isBatchMode || !SystemInfo.supportsAsyncGPUReadback)
            {
                Assert.Ignore("Headless or no AsyncGPUReadback support; frame capture is disabled by design here. " +
                              "Run PlayMode tests from the editor GUI or verify frames via the benchmark player build.");
                yield break;
            }

            var settings = MakeSettings();
            var buffer = new SessionBuffer(60, 32);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var host = new GameObject("QamelTestHost").AddComponent<CoroutineHost>();
            var recorder = new FrameRecorder(settings, buffer, () => clock.Elapsed.TotalSeconds);
            host.StartCoroutine(recorder.CaptureLoop());

            try
            {
                // At 10 fps, 1.5 real-time seconds should yield several frames even
                // with encode latency.
                float deadline = Time.realtimeSinceStartup + 1.5f;
                var frames = new List<CapturedFrame>();
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    frames.Clear();
                    buffer.Snapshot(new List<string>(), frames);
                    if (frames.Count >= 2) break;
                }

                Assert.GreaterOrEqual(frames.Count, 1, "no frames captured");
                var frame = frames[0];
                Assert.Greater(frame.Jpg.Length, 100, "frame suspiciously small");
                // JPEG magic bytes (SOI marker).
                Assert.AreEqual(0xFF, frame.Jpg[0]);
                Assert.AreEqual(0xD8, frame.Jpg[1]);
                Assert.LessOrEqual(frame.Width, 320);
                Assert.Greater(frame.Height, 0);
            }
            finally
            {
                recorder.Dispose();
                Object.Destroy(host.gameObject);
                Object.Destroy(settings);
            }
        }

        [UnityTest]
        public IEnumerator EndToEndReportBundleFromLiveRecorders()
        {
            var settings = MakeSettings();
            var buffer = new SessionBuffer(60, 32);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            System.Func<double> now = () => clock.Elapsed.TotalSeconds;

            var logRecorder = new LogRecorder(buffer, now);
            try
            {
                Debug.Log("e2e-breadcrumb");
                buffer.AddEvent(now(), SessionEvents.Custom(now(), "level_loaded", "test_level"));
                yield return null;

                double t = now();
                buffer.AddEvent(t, SessionEvents.Report(t, "e2e0report000", "it broke"));

                var eventLines = new List<string>();
                var frames = new List<CapturedFrame>();
                buffer.Snapshot(eventLines, frames);
                byte[] bytes = ReportBundler.BuildBundle(
                    "{\"schema\":\"1\",\"kind\":\"report\",\"report_id\":\"e2e0report000\"}", eventLines, frames);

                using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
                {
                    Assert.IsNotNull(zip.GetEntry("manifest.json"));
                    string logs;
                    using (var reader = new StreamReader(zip.GetEntry("logs.jsonl").Open()))
                        logs = reader.ReadToEnd();

                    Assert.IsTrue(logs.Contains("e2e-breadcrumb"), "console log missing from bundle");
                    Assert.IsTrue(logs.Contains("\"name\":\"level_loaded\""), "custom event missing from bundle");
                    Assert.IsTrue(logs.Contains("\"report_id\":\"e2e0report000\""), "report marker missing from bundle");

                    // Every line must be valid JSON with ascending timestamps.
                    double previousT = -1;
                    foreach (var line in logs.Split('\n'))
                    {
                        if (line.Length == 0) continue;
                        var parsed = TestJsonLite.ParseT(line);
                        Assert.GreaterOrEqual(parsed, previousT, "timestamps must be ascending");
                        previousT = parsed;
                    }
                }
            }
            finally
            {
                logRecorder.Dispose();
                Object.Destroy(settings);
            }
        }
    }

    /// <summary>Minimal helper: extracts the leading "t" value of an event line.</summary>
    static class TestJsonLite
    {
        public static double ParseT(string line)
        {
            const string prefix = "{\"t\":";
            Assert.IsTrue(line.StartsWith(prefix), "event line must start with {\"t\": but was: " + line);
            int end = line.IndexOf(',', prefix.Length);
            return double.Parse(line.Substring(prefix.Length, end - prefix.Length),
                System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
