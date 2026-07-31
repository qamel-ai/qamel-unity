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
        readonly CaptureHealthCounters _health = new CaptureHealthCounters();

        /// <summary>Cumulative capture attempt / drop counters for this session.</summary>
        public CaptureHealthCounters Health => _health;

        RenderTexture _fullRt;
        RenderTexture _smallRt;
        Camera _fallbackCam;
        GameObject _fallbackCamGo;
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
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                // True headless / Null device: nothing is rendered and
                // WaitForEndOfFrame never fires. Logs and input are still captured.
                // -batchmode with a real GPU (publish tests) still captures frames.
                QLog.Info("No graphics device; gameplay frame capture is disabled.");
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
                // WaitForEndOfFrame never completes under -batchmode (CLI / publish
                // tests), so use a per-update wait there. In a normal player, wait
                // for end-of-frame when a Game view exists so screenshots are coherent.
                if (!Application.isBatchMode && Screen.width > 0 && Screen.height > 0)
                    yield return endOfFrame;
                else
                    yield return null;

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
            _health.OnAttempt();
            if (_inFlight >= MaxInFlightReadbacks)
            {
                _health.OnDropInflight();
                return;
            }

            int screenW = Screen.width;
            int screenH = Screen.height;
            // Batchmode / no Game view: ScreenCapture needs a rendered frame that
            // never arrives. Drive a tiny camera into an RT instead.
            bool useFallbackCamera =
                Application.isBatchMode || screenW <= 0 || screenH <= 0;
            if (useFallbackCamera)
            {
                screenW = Mathf.Max(64, _settings.frameWidth) & ~1;
                screenH = Mathf.Max(64, Mathf.RoundToInt(screenW * 9f / 16f)) & ~1;
            }

            EnsureTargets(screenW, screenH);

            if (useFallbackCamera)
            {
                EnsureFallbackCamera();
                _fallbackCam.targetTexture = _fullRt;
                _fallbackCam.Render();
                _fallbackCam.targetTexture = null;
            }
            else
            {
                ScreenCapture.CaptureScreenshotIntoRenderTexture(_fullRt);
            }

            var previousActive = RenderTexture.active;
            Graphics.Blit(_fullRt, _smallRt);
            RenderTexture.active = previousActive;

            double t = _now();
            long index = _frameIndex++;
            int w = _smallW;
            int h = _smallH;
            _inFlight++;
            if (useFallbackCamera)
            {
                // Async callbacks are unreliable under -batchmode PlayMode; block
                // briefly so CLI tests and early no-Game-view seconds still keep frames.
                var request = AsyncGPUReadback.Request(_smallRt, 0, TextureFormat.RGBA32);
                request.WaitForCompletion();
                OnReadback(request, t, index, w, h);
            }
            else
            {
                AsyncGPUReadback.Request(_smallRt, 0, TextureFormat.RGBA32,
                    request => OnReadback(request, t, index, w, h));
            }
        }

        void EnsureFallbackCamera()
        {
            if (_fallbackCam != null) return;
            _fallbackCamGo = new GameObject("QamelFallbackCaptureCam");
            UnityEngine.Object.DontDestroyOnLoad(_fallbackCamGo);
            _fallbackCamGo.hideFlags = HideFlags.HideAndDontSave;
            _fallbackCam = _fallbackCamGo.AddComponent<Camera>();
            _fallbackCam.enabled = false;
            _fallbackCam.clearFlags = CameraClearFlags.SolidColor;
            _fallbackCam.backgroundColor = new Color(0.12f, 0.14f, 0.18f, 1f);
            _fallbackCam.orthographic = true;
            _fallbackCam.cullingMask = 0;
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
                _fullRt.Create();
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
                _smallRt.Create();
                _smallW = w;
                _smallH = h;
            }
        }

        void OnReadback(AsyncGPUReadbackRequest request, double t, long index, int w, int h)
        {
            _inFlight = Mathf.Max(0, _inFlight - 1);
            if (_disposed) return;
            if (request.hasError)
            {
                _health.OnReadbackError();
                return;
            }

            var data = request.GetData<byte>();
            int length = w * h * 4;
            if (data.Length != length)
            {
                _health.OnReadbackError();
                return;
            }

            // The native readback data is only valid during this callback; copy it
            // into a pooled buffer for the encode thread.
            byte[] buffer = RentBuffer(length);
            data.CopyTo(buffer);

            lock (_workerGate)
            {
                if (_workerStop || _jobs.Count >= MaxQueuedEncodes)
                {
                    ReturnBufferLocked(buffer);
                    _health.OnDropEncodeQueue();
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
                    _health.OnEncodeError();
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
            if (jpg == null || jpg.Length == 0)
            {
                _health.OnEncodeError();
                return;
            }

            _sink.AddFrame(new CapturedFrame
            {
                T = job.T,
                Index = job.Index,
                Width = job.Width,
                Height = job.Height,
                Jpg = jpg,
            });
            _health.OnKept();
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
            if (_fallbackCamGo != null)
            {
                UnityEngine.Object.Destroy(_fallbackCamGo);
                _fallbackCamGo = null;
                _fallbackCam = null;
            }
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
