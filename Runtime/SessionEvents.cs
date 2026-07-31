using System;

namespace QamelCapture
{
    /// <summary>
    /// Builders for the JSONL event lines defined in the Qamel capture wire format.
    /// Thread-safe: each thread reuses its own writer.
    /// </summary>
    internal static class SessionEvents
    {
        [ThreadStatic] static QamelJson _writer;

        static QamelJson Writer
        {
            get
            {
                if (_writer == null) _writer = new QamelJson();
                return _writer;
            }
        }

        public static string Log(double t, string level, string message, string stack)
        {
            var j = Writer.Begin()
                .Num("t", t)
                .Str("type", "log")
                .Str("level", level)
                .Str("message", message);
            if (!string.IsNullOrEmpty(stack)) j.Str("stack", stack);
            return j.End();
        }

        /// <summary>
        /// ~1 Hz performance / session-health sample. Wire type stays
        /// <c>context</c> for compatibility; dashboards may label it Performance.
        /// </summary>
        public static string Context(
            double t,
            string scene,
            float fps,
            float frameMsMax,
            long memoryMb,
            float timeScale,
            float cpuFrameMs,
            float gpuFrameMs,
            CaptureHealthSnapshot capture)
        {
            var j = Writer.Begin()
                .Num("t", t)
                .Str("type", "context")
                .Str("scene", scene)
                .Num("fps", Math.Round(fps, 1))
                .Num("frame_ms_max", Math.Round(frameMsMax, 2))
                .Int("memory_mb", memoryMb)
                .Num("time_scale", timeScale)
                .Int("capture_attempted", capture.Attempted)
                .Int("capture_kept", capture.Kept)
                .Int("capture_drop_inflight", capture.DropInflight)
                .Int("capture_drop_encode", capture.DropEncodeQueue)
                .Int("capture_readback_errors", capture.ReadbackErrors)
                .Int("capture_encode_errors", capture.EncodeErrors);
            if (cpuFrameMs >= 0f) j.Num("cpu_frame_ms", Math.Round(cpuFrameMs, 2));
            if (gpuFrameMs >= 0f) j.Num("gpu_frame_ms", Math.Round(gpuFrameMs, 2));
            return j.End();
        }

        public static string Input(double t, string action, string key)
        {
            return Writer.Begin()
                .Num("t", t)
                .Str("type", "input")
                .Str("action", action)
                .Str("key", key)
                .End();
        }

        public static string MousePos(double t, float x, float y)
        {
            return Writer.Begin()
                .Num("t", t)
                .Str("type", "input")
                .Str("action", "mouse_pos")
                .Num("x", Math.Round(x, 4))
                .Num("y", Math.Round(y, 4))
                .End();
        }

        public static string Custom(double t, string name, string data)
        {
            var j = Writer.Begin()
                .Num("t", t)
                .Str("type", "custom")
                .Str("name", name);
            if (data != null) j.Str("data", data);
            return j.End();
        }

        public static string Identity(double t, string action, IdentitySnapshot identity)
        {
            return Writer.Begin()
                .Num("t", t)
                .Str("type", "identity")
                .Str("action", action)
                .Str("installation_id", identity.InstallationId ?? "")
                .Str("external_player_id", identity.ExternalPlayerId ?? "")
                .Str("participant_kind", identity.ParticipantKind ?? "unknown")
                .End();
        }

        public static string Report(double t, string reportId, string userText)
        {
            return Writer.Begin()
                .Num("t", t)
                .Str("type", "report")
                .Str("report_id", reportId)
                .Str("text", userText ?? "")
                .End();
        }
    }
}
