using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace QamelCapture
{
    /// <summary>
    /// Rolling gameplay recording. Every capture interval the screen is copied into
    /// a downscaled render target, read back asynchronously from the GPU (no stall)
    /// and JPEG-encoded on a low-priority worker thread. Main-thread cost per
    /// capture is one screenshot copy + blit + readback request; per rendered frame
    /// there is no work at all between captures.
    /// </summary>
    internal sealed class FrameRecorder : IDisposable
    {
        const int MaxInFlightReadbacks = 4;
        const int MaxQueuedEncodes = 8;
        const int MaxEncodeFailuresBeforeWarn = 3;

        readonly QamelSettings _settings;
        readonly ISessionSink _sink;
        readonly Func<double> _now;
        readonly bool _flipAuto;

        RenderTexture _fullRt;
        RenderTexture _smallRt;
        int _smallW;
        int _smallH;
        long _frameIndex;
        int _inFlight;
        float _nextCaptureAt;
        volatile bool _disposed;

        readonly object _workerGate = new object();
        readonly Queue<EncodeJob> _jobs = new Queue<EncodeJob>();
        readonly Stack<byte[]> _bufferPool = new Stack<byte[]>();
        Thread _worker;
        bool _workerStop;
        int _encodeFailures;
        byte[] _flipRow;

        struct EncodeJob
        {
            public byte[] Rgba;
            public int Width;
            public int Height;
            public double T;
            public long Index;
        }

        public FrameRecorder(QamelSettings settings, ISessionSink sink, Func<double> now)
        {
            _settings = settings;
            _sink = sink;
            _now = now;
            // Direct3D/Metal/Vulkan readbacks of render targets are typically
            // top-down; OpenGL is bottom-up. Overridable via settings.frameFlip.
            _flipAuto = SystemInfo.graphicsUVStartsAtTop;
        }

        public IEnumerator CaptureLoop()
        {
            if (Application.isBatchMode)
            {
                // Headless (batchmode/server builds): nothing is rendered and
                // WaitForEndOfFrame never fires. Logs and input are still captured.
                QLog.Info("Running headless; gameplay frame capture is disabled.");
                yield break;
            }
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                QLog.Warn("AsyncGPUReadback is not supported on this platform; gameplay frames are disabled (logs and input are still captured).");
                yield break;
            }

            var endOfFrame = new WaitForEndOfFrame();
            float interval = 1f / Mathf.Max(1f, _settings.captureFps);
            while (!_disposed)
            {
                // Screenshots must be taken at end of frame, after rendering.
                yield return endOfFrame;
                if (Time.unscaledTime < _nextCaptureAt) continue;
                _nextCaptureAt = Time.unscaledTime + interval;

                try
                {
                    Capture();
                }
                catch (Exception e)
                {
                    QLog.Warn("Frame capture failed and was disabled: " + e.Message);
                    yield break;
                }
            }
        }

        void Capture()
        {
            if (_inFlight >= MaxInFlightReadbacks) return;

            int screenW = Screen.width;
            int screenH = Screen.height;
            if (screenW <= 0 || screenH <= 0) return;

            EnsureTargets(screenW, screenH);

            ScreenCapture.CaptureScreenshotIntoRenderTexture(_fullRt);
            var previousActive = RenderTexture.active;
            Graphics.Blit(_fullRt, _smallRt);
            RenderTexture.active = previousActive;

            double t = _now();
            long index = _frameIndex++;
            int w = _smallW;
            int h = _smallH;
            _inFlight++;
            AsyncGPUReadback.Request(_smallRt, 0, TextureFormat.RGBA32,
                request => OnReadback(request, t, index, w, h));
        }

        void EnsureTargets(int screenW, int screenH)
        {
            if (_fullRt == null || _fullRt.width != screenW || _fullRt.height != screenH)
            {
                Release(ref _fullRt);
                _fullRt = new RenderTexture(screenW, screenH, 0, RenderTextureFormat.ARGB32)
                {
                    name = "QamelFullFrame",
                };
            }

            int w = Mathf.Min(Mathf.Max(16, _settings.frameWidth), screenW) & ~1;
            int h = Mathf.Max(2, Mathf.RoundToInt((float)w * screenH / screenW)) & ~1;
            if (_smallRt == null || _smallW != w || _smallH != h)
            {
                Release(ref _smallRt);
                _smallRt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
                {
                    name = "QamelSmallFrame",
                };
                _smallW = w;
                _smallH = h;
            }
        }

        void OnReadback(AsyncGPUReadbackRequest request, double t, long index, int w, int h)
        {
            _inFlight = Mathf.Max(0, _inFlight - 1);
            if (_disposed || request.hasError) return;

            var data = request.GetData<byte>();
            int length = w * h * 4;
            if (data.Length != length) return;

            // The native readback data is only valid during this callback; copy it
            // into a pooled buffer for the encode thread.
            byte[] buffer = RentBuffer(length);
            data.CopyTo(buffer);

            lock (_workerGate)
            {
                if (_workerStop || _jobs.Count >= MaxQueuedEncodes)
                {
                    ReturnBufferLocked(buffer);
                    return;
                }
                _jobs.Enqueue(new EncodeJob { Rgba = buffer, Width = w, Height = h, T = t, Index = index });
                EnsureWorkerLocked();
                Monitor.Pulse(_workerGate);
            }
        }

        byte[] RentBuffer(int length)
        {
            lock (_workerGate)
            {
                while (_bufferPool.Count > 0)
                {
                    var buffer = _bufferPool.Pop();
                    if (buffer.Length == length) return buffer;
                    // Wrong size (resolution changed): drop and keep looking.
                }
            }
            return new byte[length];
        }

        void ReturnBufferLocked(byte[] buffer)
        {
            if (_bufferPool.Count < MaxInFlightReadbacks + MaxQueuedEncodes) _bufferPool.Push(buffer);
        }

        void EnsureWorkerLocked()
        {
            if (_worker != null) return;
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "QamelFrameEncoder",
                Priority = System.Threading.ThreadPriority.BelowNormal,
            };
            _worker.Start();
        }

        void WorkerLoop()
        {
            while (true)
            {
                EncodeJob job;
                lock (_workerGate)
                {
                    while (_jobs.Count == 0 && !_workerStop) Monitor.Wait(_workerGate);
                    if (_workerStop && _jobs.Count == 0) return;
                    job = _jobs.Dequeue();
                }

                try
                {
                    Encode(job);
                }
                catch (Exception e)
                {
                    if (Interlocked.Increment(ref _encodeFailures) == MaxEncodeFailuresBeforeWarn)
                        QLog.Warn("Frame encoding keeps failing (" + e.Message + "); some gameplay frames will be missing.");
                }

                lock (_workerGate)
                {
                    ReturnBufferLocked(job.Rgba);
                }
            }
        }

        void Encode(EncodeJob job)
        {
            if (ShouldFlip()) FlipRowsInPlace(job.Rgba, job.Width, job.Height);

            byte[] jpg = ImageConversion.EncodeArrayToJPG(
                job.Rgba, GraphicsFormat.R8G8B8A8_SRGB,
                (uint)job.Width, (uint)job.Height, 0, _settings.jpegQuality);
            if (jpg == null || jpg.Length == 0) return;

            _sink.AddFrame(new CapturedFrame
            {
                T = job.T,
                Index = job.Index,
                Width = job.Width,
                Height = job.Height,
                Jpg = jpg,
            });
        }

        bool ShouldFlip()
        {
            switch (_settings.frameFlip)
            {
                case QamelSettings.FlipMode.ForceFlip: return true;
                case QamelSettings.FlipMode.NoFlip: return false;
                default: return _flipAuto;
            }
        }

        void FlipRowsInPlace(byte[] rgba, int width, int height)
        {
            int rowBytes = width * 4;
            if (_flipRow == null || _flipRow.Length < rowBytes) _flipRow = new byte[rowBytes];
            for (int y = 0; y < height / 2; y++)
            {
                int top = y * rowBytes;
                int bottom = (height - 1 - y) * rowBytes;
                Buffer.BlockCopy(rgba, top, _flipRow, 0, rowBytes);
                Buffer.BlockCopy(rgba, bottom, rgba, top, rowBytes);
                Buffer.BlockCopy(_flipRow, 0, rgba, bottom, rowBytes);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_workerGate)
            {
                _workerStop = true;
                Monitor.PulseAll(_workerGate);
            }
            Release(ref _fullRt);
            Release(ref _smallRt);
        }

        static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            rt = null;
        }
    }
}
