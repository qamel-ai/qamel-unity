using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#endif

namespace QamelCapture.TestAuthoring
{
    /// <summary>
    /// Bounded, in-memory native Input System capture for recorded test authoring.
    /// Report evidence input continues through InputRecorder and SessionBuffer.
    /// </summary>
    internal sealed class NewInputTraceAdapter : ITestInputTraceAdapter
    {
        public const int DefaultInitialBytes = 2 * 1024 * 1024;
        public const int DefaultMaximumBytes = 10 * 1024 * 1024;

        readonly int _initialBytes;
        readonly int _maximumBytes;
        readonly Func<bool> _captureAllowed;
        readonly TestInputReplayMode _defaultReplayMode;
        readonly Func<TestInputReplayMode> _selectReplayMode;
        readonly Func<RecordedInputFrames.Timing> _readFrameTiming;
        TestInputReplayMode _activeReplayMode;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        readonly List<InputDevice> _playbackDevices = new List<InputDevice>();
        readonly List<string> _deviceMappings = new List<string>();
        InputEventTrace _trace;
        InputEventTrace _replayTrace;
        InputEventTrace.ReplayController _replayController;
        long _recordedEventCount;
        long _replayedEventCount;
        long _expectedReplayStateEvents;
        long _physicalInputEventCount;
        float _recordedFrameDeltaSeconds;
        float _previousCaptureDeltaTime;
        int _previousTargetFrameRate;
        int _previousVSyncCount;
        bool _overrodeReplayPacing;
        readonly List<RecordedInputFrames.Timing> _capturedFrames = new List<RecordedInputFrames.Timing>();
        RecordedInputFrames.Timing[] _replayFrames;
        InputReplayPlayerLoop _playerLoop;
        float _previousTimeScale;
        float _previousFixedDeltaTime;
        bool _finishPending;
        int _replayFrameCount;
#endif
        bool _disposed;

        public NewInputTraceAdapter(
            int initialBytes = DefaultInitialBytes,
            int maximumBytes = DefaultMaximumBytes,
            Func<bool> captureAllowed = null,
            TestInputReplayMode replayMode = TestInputReplayMode.RecordedFrames,
            Func<RecordedInputFrames.Timing> readFrameTiming = null,
            Func<TestInputReplayMode> selectReplayMode = null)
        {
            if (initialBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(initialBytes));
            if (maximumBytes < initialBytes)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            if (!Enum.IsDefined(typeof(TestInputReplayMode), replayMode))
                throw new ArgumentOutOfRangeException(nameof(replayMode));

            _initialBytes = initialBytes;
            _maximumBytes = maximumBytes;
            _captureAllowed = captureAllowed;
            _defaultReplayMode = replayMode;
            _activeReplayMode = replayMode;
            _selectReplayMode = selectReplayMode;
            _readFrameTiming = readFrameTiming ?? (() => new RecordedInputFrames.Timing
            {
                DeltaTime = Time.deltaTime,
                TimeScale = Time.timeScale,
                FixedDeltaTime = Time.fixedDeltaTime
            });
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            Status = TestInputTraceStatus.Idle;
            ReplayStatus = TestInputReplayStatus.Idle;
#else
            Status = TestInputTraceStatus.Unavailable;
            ReplayStatus = TestInputReplayStatus.Error;
#endif
        }

        public static bool IsSupportedByCurrentConfiguration
        {
            get
            {
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                return true;
#else
                return false;
#endif
            }
        }

        public static string CurrentConfigurationUnavailableReason
        {
            get
            {
#if QAMEL_INPUT_SYSTEM
                const bool packageAvailable = true;
#else
                const bool packageAvailable = false;
#endif
#if ENABLE_INPUT_SYSTEM
                const bool inputSystemEnabled = true;
#else
                const bool inputSystemEnabled = false;
#endif
                return ConfigurationUnavailableReasonFor(
                    packageAvailable,
                    inputSystemEnabled);
            }
        }

        public bool IsAvailable => IsSupportedByCurrentConfiguration;
        public string UnavailableReason => CurrentConfigurationUnavailableReason;
        public TestInputTraceStatus Status { get; private set; }
        public TestInputReplayStatus ReplayStatus { get; private set; }
        public TestInputTraceErrorCode ReplayErrorCode { get; private set; }
        public string ReplayError { get; private set; }
        internal int ReplayFrameCount
        {
            get
            {
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                return _replayFrameCount;
#else
                return 0;
#endif
            }
        }

        public TestInputReplayDiagnostics ReplayDiagnostics
        {
            get
            {
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                return new TestInputReplayDiagnostics(
                    ReplayModeDescription(),
                    _replayedEventCount,
                    _physicalInputEventCount,
                    _deviceMappings);
#else
                return TestInputReplayDiagnostics.Empty();
#endif
            }
        }

        internal static string ConfigurationUnavailableReasonFor(
            bool packageAvailable,
            bool inputSystemEnabled)
        {
            if (!packageAvailable)
            {
                return "Recorded test input requires Unity's Input System package.";
            }
            if (!inputSystemEnabled)
            {
                return "Recorded test input requires Player Settings > Active Input " +
                       "Handling to include Input System Package (New) or Both.";
            }
            return null;
        }

        public TestInputTraceMetrics Metrics
        {
            get
            {
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                return BuildMetrics();
#else
                return EmptyMetrics();
#endif
            }
        }

        public TestInputTraceResult StartCapture()
        {
            if (!CheckUsable(out var unavailable))
                return unavailable;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            if (InputSystem.settings.updateMode != InputSettings.UpdateMode.ProcessEventsInDynamicUpdate)
                return Fail(TestInputTraceErrorCode.InvalidState,
                    "Recorded gameplay currently requires Input System processing in Dynamic Update.");
            try
            {
                _trace?.Dispose();
                _trace = new InputEventTrace(
                    _initialBytes,
                    growBuffer: true,
                    maxBufferSizeInBytes: _maximumBytes);
                _trace.recordFrameMarkers = true;
                _trace.onFilterEvent = AcceptReplayEvent;
                _trace.onEvent += OnEventRecorded;
                _recordedEventCount = 0;
                _capturedFrames.Clear();
                _trace.Enable();
                Status = TestInputTraceStatus.Capturing;
                return TestInputTraceResult.Success();
            }
            catch (Exception exception)
            {
                return Fail(TestInputTraceErrorCode.InvalidState,
                    "Input capture could not start: " + exception.Message);
            }
#else
            return unavailable;
#endif
        }

        public TestInputTraceResult PauseCapture()
        {
            if (!CheckUsable(out var unavailable))
                return unavailable;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            if (Status != TestInputTraceStatus.Capturing || _trace == null)
                return TestInputTraceResult.Failure(
                    TestInputTraceErrorCode.InvalidState,
                    "Input capture is not running.");

            _trace.Disable();
            Status = TestInputTraceStatus.Paused;
            return TestInputTraceResult.Success();
#else
            return unavailable;
#endif
        }

        public TestInputTraceResult ResumeCapture()
        {
            if (!CheckUsable(out var unavailable))
                return unavailable;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            if (Status != TestInputTraceStatus.Paused || _trace == null)
                return TestInputTraceResult.Failure(
                    TestInputTraceErrorCode.InvalidState,
                    "Input capture is not paused.");

            try
            {
                _trace.Enable();
                Status = TestInputTraceStatus.Capturing;
                return TestInputTraceResult.Success();
            }
            catch (Exception exception)
            {
                return Fail(TestInputTraceErrorCode.InvalidState,
                    "Input capture could not resume: " + exception.Message);
            }
#else
            return unavailable;
#endif
        }

        public TestInputTraceSnapshotResult PauseAndSnapshot()
        {
            var pause = PauseCapture();
            if (!pause.Succeeded)
                return TestInputTraceSnapshotResult.Failure(pause.ErrorCode, pause.Error);

#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var metrics = BuildMetrics();
            if (metrics.OverwroteEvents)
            {
                return TestInputTraceSnapshotResult.Failure(
                    TestInputTraceErrorCode.Overwritten,
                    "The input buffer overwrote actions recorded after the state anchor. " +
                    "Capture a new starting state before creating a draft.");
            }
            if (metrics.RetainedStateEventCount == 0)
            {
                return TestInputTraceSnapshotResult.Failure(
                    TestInputTraceErrorCode.Empty,
                    "No keyboard, mouse, or gamepad actions were recorded after the state anchor.");
            }

            try
            {
                using (var stream = new MemoryStream())
                {
                    _trace.WriteTo(stream);
                    return TestInputTraceSnapshotResult.Success(
                        new TestInputTraceSnapshot(
                            RecordedInputFrames.Write(stream.ToArray(), _capturedFrames), metrics));
                }
            }
            catch (Exception exception)
            {
                return TestInputTraceSnapshotResult.Failure(
                    TestInputTraceErrorCode.SerializationFailed,
                    "The in-memory input snapshot could not be created: " + exception.Message);
            }
#else
            return TestInputTraceSnapshotResult.Failure(
                TestInputTraceErrorCode.Unavailable, UnavailableReason);
#endif
        }

        public TestInputTraceResult StartReplay(TestInputTraceSnapshot snapshot)
        {
            if (!CheckUsable(out var unavailable))
                return unavailable;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            if (snapshot == null)
            {
                return ReplayFailure(
                    TestInputTraceErrorCode.Empty,
                    "A frozen input snapshot is required before replay.");
            }
            if (Status != TestInputTraceStatus.Paused || _trace == null)
            {
                return ReplayFailure(
                    TestInputTraceErrorCode.InvalidState,
                    "Background input capture must be paused before replay.");
            }
            if (ReplayStatus == TestInputReplayStatus.Replaying)
            {
                return ReplayFailure(
                    TestInputTraceErrorCode.InvalidState,
                    "Input replay is already running.");
            }

            CleanupReplay();
            ResetReplayDiagnostics();
            try
            {
                _activeReplayMode = _selectReplayMode?.Invoke() ?? _defaultReplayMode;
                if (!Enum.IsDefined(typeof(TestInputReplayMode), _activeReplayMode))
                    throw new InvalidOperationException("The selected replay mode is invalid.");
            }
            catch (Exception exception)
            {
                return ReplayFailure(
                    TestInputTraceErrorCode.InvalidState,
                    "Input replay mode selection failed: " + exception.Message);
            }
            try
            {
                _replayFrames = RecordedInputFrames.Read(snapshot.GetBytesCopy(), out var nativeTrace);
                using (var stream = new MemoryStream(nativeTrace, writable: false))
                    _replayTrace = InputEventTrace.LoadFrom(stream);
                if (_replayFrames != null)
                {
                    var markers = 0;
                    foreach (var eventPtr in _replayTrace)
                        if (eventPtr.type == InputEventTrace.FrameMarkerEvent) markers++;
                    if (markers != _replayFrames.Length)
                        throw new InvalidDataException("Input frame markers and simulation timing do not match.");
                }
                _recordedFrameDeltaSeconds = EstimateRecordedFrameDelta(_replayTrace);
            }
            catch (Exception exception)
            {
                return ReplayFailure(
                    TestInputTraceErrorCode.DeserializationFailed,
                    "The frozen input snapshot could not be loaded: " + exception.Message);
            }

            var mappings = BuildDeviceMappings(_replayTrace);
            if (!mappings.Succeeded)
                return mappings;
            ResetPlaybackDevices();

            try
            {
                _replayController = _replayTrace.Replay()
                    .OnEvent(OnReplayEventQueued)
                    .OnFinished(OnReplayFinished);
                var infos = _replayTrace.deviceInfos;
                for (var index = 0; index < infos.Count; index++)
                {
                    _replayController.WithDeviceMappedFromTo(
                        infos[index].deviceId,
                        _playbackDevices[index].deviceId);
                }

                InputSystem.onEvent += OnInputEventDuringReplay;
                ReplayStatus = TestInputReplayStatus.Replaying;
                // Subscribe before the native controller, including in the Editor,
                // where editor-only input updates must not consume gameplay frames.
                InputSystem.onBeforeUpdate += BeforeReplayInputUpdate;
                _playerLoop = new InputReplayPlayerLoop(FinishReplayFrame);
                if (_activeReplayMode == TestInputReplayMode.RecordedFrames)
                {
                    ApplyRecordedFramePacing();
                    _replayController.PlayAllFramesOneByOne();
                }
                else
                {
                    _replayController.PlayAllEventsAccordingToTimestamps();
                }
                return TestInputTraceResult.Success();
            }
            catch (Exception exception)
            {
                return ReplayFailure(
                    TestInputTraceErrorCode.ReplayFailed,
                    "Input replay could not start: " + exception.Message);
            }
#else
            return unavailable;
#endif
        }

        public bool CancelReplay()
        {
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            if (_disposed || ReplayStatus != TestInputReplayStatus.Replaying)
                return false;

            CleanupReplay();
            ReplayStatus = TestInputReplayStatus.Cancelled;
            ReplayErrorCode = TestInputTraceErrorCode.None;
            ReplayError = null;
            return true;
#else
            return false;
#endif
        }

        public void StopCapture()
        {
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            if (_trace != null)
            {
                _trace.Disable();
                _trace.Dispose();
                _trace = null;
            }
#endif
            if (!_disposed)
                Status = IsAvailable ? TestInputTraceStatus.Idle : TestInputTraceStatus.Unavailable;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            CleanupReplay();
#endif
            StopCapture();
            _disposed = true;
            Status = TestInputTraceStatus.Disposed;
            ReplayStatus = TestInputReplayStatus.Disposed;
        }

        bool CheckUsable(out TestInputTraceResult failure)
        {
            if (_disposed)
            {
                failure = TestInputTraceResult.Failure(
                    TestInputTraceErrorCode.InvalidState,
                    "The input trace adapter has been disposed.");
                return false;
            }
            if (!IsAvailable)
            {
                failure = TestInputTraceResult.Failure(
                    TestInputTraceErrorCode.Unavailable,
                    UnavailableReason);
                return false;
            }

            failure = null;
            return true;
        }

        TestInputTraceResult Fail(TestInputTraceErrorCode code, string error)
        {
            Status = TestInputTraceStatus.Error;
            return TestInputTraceResult.Failure(code, error);
        }

#if QAMEL_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        TestInputTraceMetrics BuildMetrics()
        {
            if (_trace == null)
                return EmptyMetrics();

            var hasTimes = false;
            var oldest = 0d;
            var newest = 0d;
            long stateEvents = 0;
            foreach (var eventPtr in _trace)
            {
                if (!hasTimes)
                {
                    oldest = eventPtr.time;
                    newest = eventPtr.time;
                    hasTimes = true;
                }
                else
                {
                    if (eventPtr.time < oldest) oldest = eventPtr.time;
                    if (eventPtr.time > newest) newest = eventPtr.time;
                }

                if (eventPtr.IsA<StateEvent>() || eventPtr.IsA<DeltaStateEvent>())
                    stateEvents++;
            }

            var layouts = new List<string>();
            var infos = _trace.deviceInfos;
            for (var index = 0; index < infos.Count; index++)
            {
                if (!layouts.Contains(infos[index].layout))
                    layouts.Add(infos[index].layout);
            }

            return new TestInputTraceMetrics(
                _trace.eventCount,
                _recordedEventCount,
                stateEvents,
                _trace.totalEventSizeInBytes,
                _trace.allocatedSizeInBytes,
                _trace.maxSizeInBytes,
                hasTimes,
                oldest,
                newest,
                layouts);
        }

        string ReplayModeDescription()
        {
            if (_activeReplayMode == TestInputReplayMode.RecordedTiming)
                return "Recorded timing";
            if (_replayFrames != null)
                return "Recorded frames with per-frame simulation timing";
            if (_recordedFrameDeltaSeconds <= 0)
                return "Recorded frames";

            return "Legacy recorded frames at approximately " +
                   (1f / _recordedFrameDeltaSeconds).ToString("0.#") +
                   " FPS";
        }

        static float EstimateRecordedFrameDelta(InputEventTrace trace)
        {
            var intervals = new List<double>();
            var hasPrevious = false;
            var previous = 0d;
            foreach (var eventPtr in trace)
            {
                if (eventPtr.type != InputEventTrace.FrameMarkerEvent)
                    continue;

                if (hasPrevious)
                {
                    var interval = eventPtr.time - previous;
                    if (interval >= 0.001d && interval <= 0.25d &&
                        !double.IsNaN(interval) && !double.IsInfinity(interval))
                    {
                        intervals.Add(interval);
                    }
                }

                previous = eventPtr.time;
                hasPrevious = true;
            }

            if (intervals.Count == 0)
                return 0;

            intervals.Sort();
            var middle = intervals.Count / 2;
            var median = intervals.Count % 2 == 0
                ? (intervals[middle - 1] + intervals[middle]) * 0.5d
                : intervals[middle];
            return (float)median;
        }

        void ApplyRecordedFramePacing()
        {
            if ((_recordedFrameDeltaSeconds <= 0 && _replayFrames == null) || _overrodeReplayPacing)
                return;

            _previousCaptureDeltaTime = Time.captureDeltaTime;
            _previousTargetFrameRate = Application.targetFrameRate;
            _previousVSyncCount = QualitySettings.vSyncCount;
            _previousTimeScale = Time.timeScale;
            _previousFixedDeltaTime = Time.fixedDeltaTime;
            var pacingDelta = _recordedFrameDeltaSeconds > 0
                ? _recordedFrameDeltaSeconds
                : _replayFrames[0].DeltaTime / _replayFrames[0].TimeScale;
            Time.captureDeltaTime = pacingDelta;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = Mathf.Clamp(
                Mathf.RoundToInt(1f / pacingDelta),
                5,
                240);
            _overrodeReplayPacing = true;
            PrepareReplayFrame();
        }

        internal void PrepareReplayFrame()
        {
            if (_activeReplayMode != TestInputReplayMode.RecordedFrames ||
                _replayFrames == null || _finishPending ||
                _replayFrameCount >= _replayFrames.Length)
                return;
            var timing = _replayFrames[_replayFrameCount];
            Time.timeScale = timing.TimeScale;
            Time.captureDeltaTime = timing.DeltaTime / timing.TimeScale;
            Time.fixedDeltaTime = timing.FixedDeltaTime;
        }

        void BeforeReplayInputUpdate()
        {
            var gameplayUpdate = InputState.currentUpdateType == InputUpdateType.Dynamic;
            // A countdown owner may still have time paused when StartReplay is
            // called. Capture its released scale on the first gameplay update,
            // otherwise completing replay would leave the game frozen at zero.
            if (gameplayUpdate && _replayFrameCount == 0 && _overrodeReplayPacing && _previousTimeScale <= 0)
                _previousTimeScale = Time.timeScale;
            if (_replayController != null)
                _replayController.paused = !gameplayUpdate;
            if (gameplayUpdate && !_finishPending)
                _replayFrameCount++;
        }

        internal void FinishReplayFrame()
        {
            if (ReplayStatus != TestInputReplayStatus.Replaying)
                return;
            if (!_finishPending)
            {
                // Set the next step before Unity advances its native clock.
                // Some player loops advance time before managed TimeUpdate.
                PrepareReplayFrame();
                return;
            }
            CleanupReplay();
            ReplayStatus = TestInputReplayStatus.Completed;
            ReplayErrorCode = TestInputTraceErrorCode.None;
            ReplayError = null;
        }

        void RestoreFramePacing()
        {
            if (!_overrodeReplayPacing)
                return;

            Time.captureDeltaTime = _previousCaptureDeltaTime;
            Application.targetFrameRate = _previousTargetFrameRate;
            QualitySettings.vSyncCount = _previousVSyncCount;
            Time.timeScale = _previousTimeScale;
            Time.fixedDeltaTime = _previousFixedDeltaTime;
            _previousCaptureDeltaTime = 0;
            _previousTargetFrameRate = 0;
            _previousVSyncCount = 0;
            _overrodeReplayPacing = false;
        }

        bool AcceptReplayEvent(InputEventPtr eventPtr, InputDevice device)
        {
            if (InputState.currentUpdateType != InputUpdateType.Dynamic)
                return false;
            if (_captureAllowed != null && !_captureAllowed())
                return false;
            if (eventPtr.type == InputEventTrace.FrameMarkerEvent)
                return true;
            if (!(device is Keyboard) && !(device is Mouse) && !(device is Gamepad))
                return false;
            return eventPtr.IsA<StateEvent>() || eventPtr.IsA<DeltaStateEvent>();
        }

        void OnEventRecorded(InputEventPtr eventPtr)
        {
            _recordedEventCount++;
            if (eventPtr.type == InputEventTrace.FrameMarkerEvent &&
                _capturedFrames.Count <= _maximumBytes / 20)
            {
                _capturedFrames.Add(_readFrameTiming());
            }
        }

        TestInputTraceResult BuildDeviceMappings(InputEventTrace replayTrace)
        {
            var infos = replayTrace.deviceInfos;
            if (infos.Count == 0)
            {
                return ReplayFailure(
                    TestInputTraceErrorCode.DeviceMappingFailed,
                    "The frozen input snapshot contains no recorded device metadata.");
            }

            for (var infoIndex = 0; infoIndex < infos.Count; infoIndex++)
            {
                var info = infos[infoIndex];
                var playbackDevice = FindPlaybackDevice(info.deviceId, info.layout);
                if (playbackDevice == null)
                {
                    return ReplayFailure(
                        TestInputTraceErrorCode.DeviceMappingFailed,
                        $"No active input device maps recorded layout '{info.layout}'.");
                }

                _playbackDevices.Add(playbackDevice);
                _deviceMappings.Add(info.layout + " -> " + playbackDevice.displayName);
            }

            return TestInputTraceResult.Success();
        }

        InputDevice FindPlaybackDevice(int recordedDeviceId, string recordedLayout)
        {
            InputDevice original = null;
            InputDevice layoutMatch = null;
            for (var deviceIndex = 0; deviceIndex < InputSystem.devices.Count; deviceIndex++)
            {
                var candidate = InputSystem.devices[deviceIndex];
                if (!candidate.added || _playbackDevices.Contains(candidate))
                    continue;
                if (!LayoutMatches(candidate.layout, recordedLayout))
                    continue;

                if (candidate.deviceId == recordedDeviceId)
                {
                    original = candidate;
                    break;
                }

                if (layoutMatch == null)
                    layoutMatch = candidate;
            }

            return original ?? layoutMatch;
        }

        static bool LayoutMatches(string playbackLayout, string recordedLayout)
        {
            return string.Equals(playbackLayout, recordedLayout, StringComparison.Ordinal) ||
                   InputSystem.IsFirstLayoutBasedOnSecond(playbackLayout, recordedLayout);
        }

        void OnReplayEventQueued(InputEventPtr eventPtr)
        {
            if (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>())
                return;

            _replayedEventCount++;
            _expectedReplayStateEvents++;
        }

        void OnInputEventDuringReplay(InputEventPtr eventPtr, InputDevice device)
        {
            if (ReplayStatus != TestInputReplayStatus.Replaying ||
                device == null || !_playbackDevices.Contains(device) ||
                (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>()))
                return;

            if (_expectedReplayStateEvents > 0)
            {
                _expectedReplayStateEvents--;
                return;
            }

            _physicalInputEventCount++;
        }

        void OnReplayFinished()
        {
            if (ReplayStatus != TestInputReplayStatus.Replaying)
                return;

            // The native callback fires while final events are only queued.
            // Do not release buttons or dispose Editor passthrough before the
            // game has consumed that input and simulated its final frame.
            _finishPending = true;
        }

        TestInputTraceResult ReplayFailure(TestInputTraceErrorCode code, string error)
        {
            CleanupReplay();
            ReplayStatus = TestInputReplayStatus.Error;
            ReplayErrorCode = code;
            ReplayError = error;
            return TestInputTraceResult.Failure(code, error);
        }

        void ResetReplayDiagnostics()
        {
            _playbackDevices.Clear();
            _deviceMappings.Clear();
            _replayedEventCount = 0;
            _expectedReplayStateEvents = 0;
            _physicalInputEventCount = 0;
            _recordedFrameDeltaSeconds = 0;
            _replayFrames = null;
            _replayFrameCount = 0;
            ReplayStatus = TestInputReplayStatus.Idle;
            ReplayErrorCode = TestInputTraceErrorCode.None;
            ReplayError = null;
        }

        void CleanupReplay()
        {
            InputSystem.onEvent -= OnInputEventDuringReplay;
            InputSystem.onBeforeUpdate -= BeforeReplayInputUpdate;
            _playerLoop?.Dispose();
            _playerLoop = null;
            _finishPending = false;
            _replayController?.Dispose();
            _replayController = null;
            RestoreFramePacing();

            ResetPlaybackDevices();

            _replayTrace?.Dispose();
            _replayTrace = null;
            _expectedReplayStateEvents = 0;
        }

        void ResetPlaybackDevices()
        {
            for (var index = 0; index < _playbackDevices.Count; index++)
            {
                var device = _playbackDevices[index];
                if (device != null && device.added)
                    InputSystem.ResetDevice(device);
            }
        }
#endif

        TestInputTraceMetrics EmptyMetrics()
        {
            return new TestInputTraceMetrics(
                0, 0, 0, 0, 0, _maximumBytes, false, 0, 0, Array.Empty<string>());
        }
    }
}
