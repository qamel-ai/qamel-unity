using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using QamelCapture.TestAuthoring;
using UnityEngine;
using UnityEngine.Rendering;

namespace QamelCapture.Editor.TestAuthoring
{
    internal enum TestEvidenceCaptureState
    {
        Recording,
        Finalizing,
        Succeeded,
        Failed,
        Cancelled,
    }

    internal sealed class TestEvidenceCapture : IDisposable
    {
        const float CaptureFps = 6f;
        const int FrameWidth = 640;
        const int JpegQuality = 55;
        const double MaxDurationSeconds = 10 * 60;
        const int MaxBundleBytes = 96 * 1024 * 1024;
        const int TargetFrameBytes = 90 * 1024 * 1024;
        const double FinalizeTimeoutSeconds = 10;

        readonly SessionBuffer _buffer;
        readonly QamelSettings _settings;
        readonly FrameRecorder _recorder;
        readonly System.Diagnostics.Stopwatch _clock;
        readonly TestEvidenceCaptureHost _host;
        double _durationSeconds;
        double _finalizeDeadline;
        bool _bundleStarted;
        volatile bool _bundleFinished;
        byte[] _completedBundle;
        string _bundleError;
        int _frameCount;
        int _frameWidth;
        int _frameHeight;
        TestEvidenceCoverage _coverage = TestEvidenceCoverage.Unknown;
        bool _captureObjectsCleaned;
        bool _disposed;

        public TestEvidenceCapture()
        {
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType ==
                GraphicsDeviceType.Null)
                throw new InvalidOperationException(
                    "Test video evidence needs a rendered Game view.");
            if (!SystemInfo.supportsAsyncGPUReadback)
                throw new InvalidOperationException(
                    "This graphics device cannot capture Test video evidence without blocking gameplay.");

            _settings = ScriptableObject.CreateInstance<QamelSettings>();
            _settings.hideFlags = HideFlags.HideAndDontSave;
            _settings.captureFps = CaptureFps;
            _settings.frameWidth = FrameWidth;
            _settings.jpegQuality = JpegQuality;
            _settings.frameFlip = QamelSettings.FlipMode.Auto;
            _buffer = new SessionBuffer(
                MaxDurationSeconds,
                Mathf.CeilToInt((float)MaxDurationSeconds * CaptureFps) + 8);
            _clock = System.Diagnostics.Stopwatch.StartNew();
            _recorder = new FrameRecorder(
                _settings,
                _buffer,
                () => _clock.Elapsed.TotalSeconds);

            var hostObject = new GameObject("QamelTestEvidenceCapture")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            UnityEngine.Object.DontDestroyOnLoad(hostObject);
            _host = hostObject.AddComponent<TestEvidenceCaptureHost>();
            _host.Begin(_recorder.CaptureLoop());
            State = TestEvidenceCaptureState.Recording;
            Status = "Recording the latest bounded gameplay video evidence.";
        }

        public TestEvidenceCaptureState State { get; private set; }
        public string Status { get; private set; }
        public TestEvidenceClip Clip { get; private set; }
        public bool IsFinished => State == TestEvidenceCaptureState.Succeeded ||
                                  State == TestEvidenceCaptureState.Failed ||
                                  State == TestEvidenceCaptureState.Cancelled;

        public void Stop()
        {
            if (_disposed || State != TestEvidenceCaptureState.Recording) return;
            _durationSeconds = _clock.Elapsed.TotalSeconds;
            _host.StopCapture();
            State = TestEvidenceCaptureState.Finalizing;
            Status = "Finalizing gameplay video evidence in memory.";
            _finalizeDeadline = UnityEditor.EditorApplication.timeSinceStartup +
                                FinalizeTimeoutSeconds;
        }

        public void Tick()
        {
            if (_disposed || IsFinished) return;
            if (State == TestEvidenceCaptureState.Recording) return;
            if (State != TestEvidenceCaptureState.Finalizing) return;

            if (!_recorder.IsIdle)
            {
                if (UnityEditor.EditorApplication.timeSinceStartup > _finalizeDeadline)
                    Fail("Timed out while finishing captured video frames.");
                return;
            }

            if (!_bundleStarted)
            {
                StartBundleBuild();
                return;
            }
            if (!_bundleFinished) return;
            if (!string.IsNullOrWhiteSpace(_bundleError))
            {
                Fail(_bundleError);
                return;
            }
            try
            {
                Clip = new TestEvidenceClip(
                    _completedBundle,
                    Math.Max(_durationSeconds, 1d / CaptureFps),
                    CaptureFps,
                    _frameWidth,
                    _frameHeight,
                    _frameCount,
                    _coverage);
                State = TestEvidenceCaptureState.Succeeded;
                Status = "Gameplay video evidence is ready to upload.";
                CleanupCaptureObjects();
            }
            catch (Exception exception)
            {
                Fail("Could not finalize Test video evidence: " + exception.Message);
            }
        }

        void StartBundleBuild()
        {
            _bundleStarted = true;
            var events = new List<string>();
            var frames = new List<CapturedFrame>();
            _buffer.Snapshot(events, frames);
            if (frames.Count == 0)
            {
                _bundleError = "Unity did not capture any gameplay frames for Test evidence.";
                _bundleFinished = true;
                return;
            }

            // Evidence is intentionally bounded independently from Test duration.
            // Keep the newest frames so long demonstrations can still become Tests.
            long retainedBytes = 16 * 1024;
            int firstRetainedFrame = frames.Count;
            for (int index = frames.Count - 1; index >= 0; index--)
            {
                long nextBytes = frames[index].Jpg.Length + 128L;
                if (firstRetainedFrame < frames.Count &&
                    retainedBytes + nextBytes > TargetFrameBytes)
                    break;
                retainedBytes += nextBytes;
                firstRetainedFrame = index;
            }
            bool exceededDurationBoundary = _durationSeconds > MaxDurationSeconds;
            if (firstRetainedFrame > 0)
                frames.RemoveRange(0, firstRetainedFrame);
            _coverage = firstRetainedFrame > 0 || exceededDurationBoundary
                ? TestEvidenceCoverage.Truncated
                : TestEvidenceCoverage.Complete;

            var last = frames[frames.Count - 1];
            _frameCount = frames.Count;
            _frameWidth = last.Width;
            _frameHeight = last.Height;
            _durationSeconds = Math.Max(_frameCount / CaptureFps, 1d / CaptureFps);
            string manifest = new QamelJson().Begin()
                .Str("schema", ReportBundler.SchemaVersion)
                .Str("kind", "test_evidence")
                .Num("duration_seconds", _durationSeconds)
                .Num("capture_fps", CaptureFps)
                .Str("coverage", CoverageValue(_coverage))
                .Int("frame_width", _frameWidth)
                .Int("frame_height", _frameHeight)
                .Int("frame_count", _frameCount)
                .End();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var bytes = ReportBundler.BuildBundle(manifest, events, frames);
                    if (bytes.Length > MaxBundleBytes)
                    {
                        _bundleError = "Test video evidence exceeded the current 96 MiB upload boundary.";
                    }
                    else
                    {
                        _completedBundle = bytes;
                    }
                }
                catch (Exception exception)
                {
                    _bundleError = "Could not build Test video evidence: " + exception.Message;
                }
                finally
                {
                    _bundleFinished = true;
                }
            });
        }

        static string CoverageValue(TestEvidenceCoverage coverage)
        {
            switch (coverage)
            {
                case TestEvidenceCoverage.Complete: return "complete";
                case TestEvidenceCoverage.Truncated: return "truncated";
                default: return "unknown";
            }
        }

        public void Cancel()
        {
            if (_disposed || IsFinished) return;
            State = TestEvidenceCaptureState.Cancelled;
            Status = "Test video evidence capture cancelled.";
            CleanupCaptureObjects();
        }

        void Fail(string message)
        {
            State = TestEvidenceCaptureState.Failed;
            Status = message;
            CleanupCaptureObjects();
        }

        void CleanupCaptureObjects()
        {
            if (_captureObjectsCleaned) return;
            _captureObjectsCleaned = true;
            _host?.StopCapture();
            _recorder?.Dispose();
            if (_host != null)
                UnityEngine.Object.Destroy(_host.gameObject);
            if (_settings != null)
                UnityEngine.Object.Destroy(_settings);
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (!IsFinished) Cancel();
            CleanupCaptureObjects();
            _disposed = true;
        }
    }

    internal sealed class TestEvidenceCaptureHost : MonoBehaviour
    {
        Coroutine _capture;

        public void Begin(IEnumerator loop)
        {
            _capture = StartCoroutine(loop);
        }

        public void StopCapture()
        {
            if (_capture == null) return;
            StopCoroutine(_capture);
            _capture = null;
        }
    }
}
