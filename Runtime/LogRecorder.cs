using System;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace QamelCapture
{
    /// <summary>
    /// Captures console logs and exceptions (from any thread) and samples ambient
    /// performance / session health (scene, fps, memory, capture drops, frame
    /// timings) about once per second.
    /// </summary>
    internal sealed class LogRecorder : IDisposable
    {
        const int MaxMessageChars = 2000;
        const int MaxStackChars = 6000;
        const float ContextInterval = 1f;
        const int MaxFrameTimings = 32;

        readonly ISessionSink _sink;
        readonly Func<double> _now;
        readonly Func<CaptureHealthSnapshot> _captureHealth;
        readonly Action _onException;
        readonly FrameTiming[] _frameTimings = new FrameTiming[MaxFrameTimings];

        float _nextContextAt;
        float _smoothedDelta = 1f / 60f;
        float _maxDeltaInInterval;
        bool _frameTimingEnabled;
        volatile bool _disposed;

        /// <param name="onException">
        /// Invoked whenever an unhandled exception is captured. May be called from
        /// any thread; the callback must be thread-safe.
        /// </param>
        /// <param name="captureHealth">
        /// Optional snapshot of gameplay-capture counters (attempted / dropped).
        /// </param>
        public LogRecorder(
            ISessionSink sink,
            Func<double> now,
            Action onException = null,
            Func<CaptureHealthSnapshot> captureHealth = null)
        {
            _sink = sink;
            _now = now;
            _onException = onException;
            _captureHealth = captureHealth ?? (() => default(CaptureHealthSnapshot));
            Application.logMessageReceivedThreaded += OnLogMessage;
            TryEnableFrameTiming();
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
            float dt = Time.unscaledDeltaTime;
            _smoothedDelta = Mathf.Lerp(_smoothedDelta, dt, 0.05f);
            if (dt > _maxDeltaInInterval) _maxDeltaInInterval = dt;

            if (_frameTimingEnabled)
                FrameTimingManager.CaptureFrameTimings();

            if (Time.unscaledTime < _nextContextAt) return;
            _nextContextAt = Time.unscaledTime + ContextInterval;

            long memoryMb = Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
            if (memoryMb <= 0) memoryMb = GC.GetTotalMemory(false) / (1024 * 1024);
            float fps = _smoothedDelta > 0.0001f ? 1f / _smoothedDelta : 0f;
            float frameMsMax = _maxDeltaInInterval * 1000f;
            _maxDeltaInInterval = 0f;

            float cpuFrameMs = -1f;
            float gpuFrameMs = -1f;
            SampleFrameTimings(out cpuFrameMs, out gpuFrameMs);

            double t = _now();
            _sink.AddEvent(t, SessionEvents.Context(
                t,
                SceneManager.GetActiveScene().name,
                fps,
                frameMsMax,
                memoryMb,
                Time.timeScale,
                cpuFrameMs,
                gpuFrameMs,
                _captureHealth()));
        }

        void TryEnableFrameTiming()
        {
            try
            {
                // Available in player builds; returns timings when the platform supports it.
                FrameTimingManager.CaptureFrameTimings();
                _frameTimingEnabled = true;
            }
            catch
            {
                _frameTimingEnabled = false;
            }
        }

        void SampleFrameTimings(out float cpuFrameMs, out float gpuFrameMs)
        {
            cpuFrameMs = -1f;
            gpuFrameMs = -1f;
            if (!_frameTimingEnabled) return;

            try
            {
                uint count = FrameTimingManager.GetLatestTimings(
                    (uint)_frameTimings.Length, _frameTimings);
                if (count == 0) return;

                double cpuSum = 0;
                double gpuSum = 0;
                uint cpuN = 0;
                uint gpuN = 0;
                for (uint i = 0; i < count; i++)
                {
                    // Unity reports seconds.
                    double cpu = _frameTimings[i].cpuFrameTime;
                    double gpu = _frameTimings[i].gpuFrameTime;
                    if (cpu > 0)
                    {
                        cpuSum += cpu;
                        cpuN++;
                    }
                    if (gpu > 0)
                    {
                        gpuSum += gpu;
                        gpuN++;
                    }
                }
                if (cpuN > 0) cpuFrameMs = (float)(cpuSum / cpuN * 1000.0);
                if (gpuN > 0) gpuFrameMs = (float)(gpuSum / gpuN * 1000.0);
            }
            catch
            {
                _frameTimingEnabled = false;
            }
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
