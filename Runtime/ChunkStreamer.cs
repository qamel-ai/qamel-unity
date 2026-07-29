using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace QamelCapture
{
    /// <summary>
    /// Experimental continuous streaming: periodically drains the session buffer
    /// and delivers each slice as a chunk bundle (kind "chunk", same wire format
    /// as reports; see the Qamel capture wire format). This is the second ISessionSink
    /// consumer the architecture was designed for; recorders are untouched.
    /// Bundling runs on the thread pool; the callback receives the finished bytes
    /// on that worker thread.
    /// </summary>
    internal sealed class ChunkStreamer
    {
        readonly QamelSettings _settings;
        readonly SessionBuffer _buffer;
        readonly Func<double> _now;
        readonly Func<IdentitySnapshot> _identity;
        readonly string _sessionId;
        readonly DateTime _sessionStartUtc;
        readonly Action<string, byte[], string> _onBundleReady;

        int _chunkIndex;
        double _lastFlushT;
        volatile bool _stopped;

        /// <param name="onBundleReady">
        /// Called on a worker thread with (manifestJson, zipBytes, fileName).
        /// The runner forwards these to the uploader; the benchmark discards them.
        /// </param>
        public ChunkStreamer(QamelSettings settings, SessionBuffer buffer, Func<double> now,
            Func<IdentitySnapshot> identity,
            string sessionId, DateTime sessionStartUtc, Action<string, byte[], string> onBundleReady)
        {
            _settings = settings;
            _buffer = buffer;
            _now = now;
            _identity = identity;
            _sessionId = sessionId;
            _sessionStartUtc = sessionStartUtc;
            _onBundleReady = onBundleReady;
        }

        public IEnumerator StreamLoop()
        {
            var wait = new WaitForSecondsRealtime(Mathf.Max(2f, _settings.streamChunkSeconds));
            while (!_stopped)
            {
                yield return wait;
                try
                {
                    Flush();
                }
                catch (Exception e)
                {
                    QLog.Warn("Streaming disabled after an internal error: " + e.Message);
                    yield break;
                }
            }
        }

        public void Stop()
        {
            _stopped = true;
        }

        /// <summary>Drains everything captured since the last flush into one chunk.</summary>
        public void Flush()
        {
            if (_stopped) return;

            var events = new List<string>();
            var frames = new List<CapturedFrame>();
            _buffer.Drain(events, frames);
            if (events.Count == 0 && frames.Count == 0) return;

            double start = _lastFlushT;
            double end = _now();
            _lastFlushT = end;
            int index = _chunkIndex++;

            int frameW = 0, frameH = 0;
            if (frames.Count > 0)
            {
                var last = frames[frames.Count - 1];
                frameW = last.Width;
                frameH = last.Height;
            }

            string manifest = ReportManifest.BuildChunk(new ChunkManifestData
            {
                SessionId = _sessionId,
                ChunkIndex = index,
                ChunkStartT = start,
                ChunkEndT = end,
                EventCount = events.Count,
                FrameCount = frames.Count,
                FrameWidth = frameW,
                FrameHeight = frameH,
                SessionStartedUtc = _sessionStartUtc,
                CaptureFps = _settings.captureFps,
                Identity = _identity(),
                BuildId = _settings.buildId,
            });
            string fileName = "chunk_" + _sessionId.Substring(0, 8) + "_" + index.ToString("D5") + ".zip";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    byte[] bytes = ReportBundler.BuildBundle(manifest, events, frames);
                    _onBundleReady(manifest, bytes, fileName);
                }
                catch (Exception e)
                {
                    QLog.Warn("Failed to build chunk bundle: " + e.Message);
                }
            });
        }
    }
}
