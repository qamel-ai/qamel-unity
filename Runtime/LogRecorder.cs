using System;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace QamelCapture
{
    /// <summary>
    /// Captures console logs and exceptions (from any thread) and samples ambient
    /// context (scene, fps, memory, time scale) about once per second.
    /// </summary>
    internal sealed class LogRecorder : IDisposable
    {
        const int MaxMessageChars = 2000;
        const int MaxStackChars = 6000;
        const float ContextInterval = 1f;

        readonly ISessionSink _sink;
        readonly Func<double> _now;
        readonly Action _onException;
        float _nextContextAt;
        float _smoothedDelta = 1f / 60f;
        volatile bool _disposed;

        /// <param name="onException">
        /// Invoked whenever an unhandled exception is captured. May be called from
        /// any thread; the callback must be thread-safe.
        /// </param>
        public LogRecorder(ISessionSink sink, Func<double> now, Action onException = null)
        {
            _sink = sink;
            _now = now;
            _onException = onException;
            Application.logMessageReceivedThreaded += OnLogMessage;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Application.logMessageReceivedThreaded -= OnLogMessage;
        }

        /// <summary>Called from the runner's Update on the main thread.</summary>
        public void Tick()
        {
            _smoothedDelta = Mathf.Lerp(_smoothedDelta, Time.unscaledDeltaTime, 0.05f);
            if (Time.unscaledTime < _nextContextAt) return;
            _nextContextAt = Time.unscaledTime + ContextInterval;

            long memoryMb = Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
            if (memoryMb <= 0) memoryMb = GC.GetTotalMemory(false) / (1024 * 1024);
            float fps = _smoothedDelta > 0.0001f ? 1f / _smoothedDelta : 0f;

            double t = _now();
            _sink.AddEvent(t, SessionEvents.Context(
                t, SceneManager.GetActiveScene().name, fps, memoryMb, Time.timeScale));
        }

        void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (_disposed) return;
            // Never capture Qamel's own output (feedback loop / noise).
            if (condition != null && condition.StartsWith(QLog.Prefix, StringComparison.Ordinal)) return;

            string level;
            switch (type)
            {
                case LogType.Error: level = "error"; break;
                case LogType.Assert: level = "assert"; break;
                case LogType.Warning: level = "warning"; break;
                case LogType.Exception: level = "exception"; break;
                default: level = "info"; break;
            }

            double t = _now();
            _sink.AddEvent(t, SessionEvents.Log(
                t, level, Truncate(condition, MaxMessageChars), Truncate(stackTrace, MaxStackChars)));

            if (type == LogType.Exception && _onException != null)
            {
                try { _onException(); }
                catch { /* a failing callback must not break the log hook */ }
            }
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "...[truncated]";
        }
    }
}
