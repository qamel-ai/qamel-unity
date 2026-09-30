using System;
using System.Collections.Generic;

namespace QamelCapture.TestAuthoring
{
    internal enum TestInputTraceStatus
    {
        Idle,
        Capturing,
        Paused,
        Error,
        Unavailable,
        Disposed
    }

    internal enum TestInputTraceErrorCode
    {
        None,
        Unavailable,
        InvalidState,
        Empty,
        Overwritten,
        SerializationFailed,
        DeserializationFailed,
        DeviceMappingFailed,
        ReplayFailed,
        ReplayTimeout
    }

    internal enum TestInputReplayStatus
    {
        Idle,
        Replaying,
        Completed,
        Cancelled,
        Error,
        Disposed
    }

    internal enum TestInputReplayMode
    {
        RecordedFrames,
        RecordedTiming
    }

    internal sealed class TestInputTraceMetrics
    {
        readonly string[] _deviceLayouts;

        public TestInputTraceMetrics(
            long retainedEventCount,
            long recordedEventCount,
            long retainedStateEventCount,
            long retainedEventBytes,
            long allocatedBytes,
            long maximumBytes,
            bool hasEventTimes,
            double oldestEventTime,
            double newestEventTime,
            IEnumerable<string> deviceLayouts)
        {
            RetainedEventCount = retainedEventCount;
            RecordedEventCount = recordedEventCount;
            RetainedStateEventCount = retainedStateEventCount;
            RetainedEventBytes = retainedEventBytes;
            AllocatedBytes = allocatedBytes;
            MaximumBytes = maximumBytes;
            HasEventTimes = hasEventTimes;
            OldestEventTime = oldestEventTime;
            NewestEventTime = newestEventTime;
            _deviceLayouts = deviceLayouts == null
                ? Array.Empty<string>()
                : new List<string>(deviceLayouts).ToArray();
        }

        public long RetainedEventCount { get; }
        public long RecordedEventCount { get; }
        public long RetainedStateEventCount { get; }
        public long RetainedEventBytes { get; }
        public long AllocatedBytes { get; }
        public long MaximumBytes { get; }
        public bool HasEventTimes { get; }
        public double OldestEventTime { get; }
        public double NewestEventTime { get; }
        public double RetainedDurationSeconds => HasEventTimes
            ? Math.Max(0, NewestEventTime - OldestEventTime)
            : 0;
        public bool OverwroteEvents => RecordedEventCount > RetainedEventCount;
        public IReadOnlyList<string> DeviceLayouts => Array.AsReadOnly(_deviceLayouts);
    }

    internal sealed class TestInputTraceSnapshot
    {
        readonly byte[] _serializedTrace;

        public TestInputTraceSnapshot(byte[] serializedTrace, TestInputTraceMetrics metrics)
        {
            if (serializedTrace == null)
                throw new ArgumentNullException(nameof(serializedTrace));
            Metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _serializedTrace = (byte[])serializedTrace.Clone();
        }

        public TestInputTraceMetrics Metrics { get; }
        public int SerializedByteCount => _serializedTrace.Length;

        public byte[] GetBytesCopy()
        {
            return (byte[])_serializedTrace.Clone();
        }
    }

    internal sealed class TestInputTraceResult
    {
        TestInputTraceResult(bool succeeded, TestInputTraceErrorCode errorCode, string error)
        {
            Succeeded = succeeded;
            ErrorCode = errorCode;
            Error = error;
        }

        public bool Succeeded { get; }
        public TestInputTraceErrorCode ErrorCode { get; }
        public string Error { get; }

        public static TestInputTraceResult Success()
        {
            return new TestInputTraceResult(true, TestInputTraceErrorCode.None, null);
        }

        public static TestInputTraceResult Failure(TestInputTraceErrorCode errorCode, string error)
        {
            return new TestInputTraceResult(false, errorCode, error);
        }
    }

    internal sealed class TestInputTraceSnapshotResult
    {
        TestInputTraceSnapshotResult(
            bool succeeded,
            TestInputTraceErrorCode errorCode,
            string error,
            TestInputTraceSnapshot snapshot)
        {
            Succeeded = succeeded;
            ErrorCode = errorCode;
            Error = error;
            Snapshot = snapshot;
        }

        public bool Succeeded { get; }
        public TestInputTraceErrorCode ErrorCode { get; }
        public string Error { get; }
        public TestInputTraceSnapshot Snapshot { get; }

        public static TestInputTraceSnapshotResult Success(TestInputTraceSnapshot snapshot)
        {
            return new TestInputTraceSnapshotResult(
                true, TestInputTraceErrorCode.None, null, snapshot);
        }

        public static TestInputTraceSnapshotResult Failure(
            TestInputTraceErrorCode errorCode,
            string error)
        {
            return new TestInputTraceSnapshotResult(false, errorCode, error, null);
        }
    }

    internal sealed class TestInputReplayDiagnostics
    {
        readonly string[] _deviceMappings;

        public TestInputReplayDiagnostics(
            string replayMode,
            long replayedEventCount,
            long physicalInputEventCount,
            IEnumerable<string> deviceMappings)
        {
            ReplayMode = string.IsNullOrWhiteSpace(replayMode)
                ? "Recorded frames"
                : replayMode;
            ReplayedEventCount = replayedEventCount;
            PhysicalInputEventCount = physicalInputEventCount;
            _deviceMappings = deviceMappings == null
                ? Array.Empty<string>()
                : new List<string>(deviceMappings).ToArray();
        }

        public string ReplayMode { get; }
        public long ReplayedEventCount { get; }
        public long PhysicalInputEventCount { get; }
        public bool PhysicalInputDetected => PhysicalInputEventCount > 0;
        public IReadOnlyList<string> DeviceMappings => Array.AsReadOnly(_deviceMappings);

        public static TestInputReplayDiagnostics Empty()
        {
            return new TestInputReplayDiagnostics(
                "Recorded frames", 0, 0, Array.Empty<string>());
        }
    }

    internal interface ITestInputTraceAdapter : IDisposable
    {
        bool IsAvailable { get; }
        string UnavailableReason { get; }
        TestInputTraceStatus Status { get; }
        TestInputTraceMetrics Metrics { get; }
        TestInputReplayStatus ReplayStatus { get; }
        TestInputReplayDiagnostics ReplayDiagnostics { get; }
        TestInputTraceErrorCode ReplayErrorCode { get; }
        string ReplayError { get; }
        TestInputTraceResult StartCapture();
        TestInputTraceResult PauseCapture();
        TestInputTraceResult ResumeCapture();
        TestInputTraceSnapshotResult PauseAndSnapshot();
        TestInputTraceResult StartReplay(TestInputTraceSnapshot snapshot);
        bool CancelReplay();
        void StopCapture();
    }
}
