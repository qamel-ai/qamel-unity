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

        public static string Context(double t, string scene, float fps, long memoryMb, float timeScale)
        {
            return Writer.Begin()
                .Num("t", t)
                .Str("type", "context")
                .Str("scene", scene)
                .Num("fps", Math.Round(fps, 1))
                .Int("memory_mb", memoryMb)
                .Num("time_scale", timeScale)
                .End();
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
