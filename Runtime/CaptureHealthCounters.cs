using System;

namespace QamelCapture
{
    /// <summary>
    /// Cumulative gameplay-capture counters for the session. Exposed on ~1 Hz
    /// context samples and rolled into the bundle manifest so we can measure
    /// how often backpressure drops frames.
    /// </summary>
    internal struct CaptureHealthSnapshot
    {
        public long Attempted;
        public long Kept;
        public long DropInflight;
        public long DropEncodeQueue;
        public long ReadbackErrors;
        public long EncodeErrors;
    }

    /// <summary>Thread-safe counters owned by <see cref="FrameRecorder"/>.</summary>
    internal sealed class CaptureHealthCounters
    {
        readonly object _gate = new object();
        long _attempted;
        long _kept;
        long _dropInflight;
        long _dropEncodeQueue;
        long _readbackErrors;
        long _encodeErrors;

        public void OnAttempt()
        {
            lock (_gate) _attempted++;
        }

        public void OnKept()
        {
            lock (_gate) _kept++;
        }

        public void OnDropInflight()
        {
            lock (_gate) _dropInflight++;
        }

        public void OnDropEncodeQueue()
        {
            lock (_gate) _dropEncodeQueue++;
        }

        public void OnReadbackError()
        {
            lock (_gate) _readbackErrors++;
        }

        public void OnEncodeError()
        {
            lock (_gate) _encodeErrors++;
        }

        public CaptureHealthSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new CaptureHealthSnapshot
                {
                    Attempted = _attempted,
                    Kept = _kept,
                    DropInflight = _dropInflight,
                    DropEncodeQueue = _dropEncodeQueue,
                    ReadbackErrors = _readbackErrors,
                    EncodeErrors = _encodeErrors,
                };
            }
        }
    }
}
