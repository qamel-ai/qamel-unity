using System.Collections.Generic;

namespace QamelCapture
{
    /// <summary>One captured gameplay frame, already JPEG-encoded.</summary>
    public sealed class CapturedFrame
    {
        public double T;
        public long Index;
        public int Width;
        public int Height;
        public byte[] Jpg;
    }

    /// <summary>
    /// Sink that recorders push timestamped items into. The rolling
    /// <see cref="SessionBuffer"/> is the default implementation; another sink can
    /// drain the same items elsewhere without touching any recorder or the wire
    /// format.
    /// </summary>
    public interface ISessionSink
    {
        void AddEvent(double t, string jsonLine);
        void AddFrame(CapturedFrame frame);
    }

    /// <summary>
    /// Thread-safe rolling buffer holding the last N seconds of events and frames.
    /// Recorders write from the main thread, the log hook and the JPEG encoder
    /// write from worker threads.
    /// </summary>
    public sealed class SessionBuffer : ISessionSink
    {
        // Safety cap so a log-spamming game cannot grow the buffer unboundedly
        // within the time window.
        const int MaxEvents = 50000;

        readonly object _gate = new object();
        readonly Queue<EventEntry> _events = new Queue<EventEntry>(1024);
        readonly Queue<CapturedFrame> _frames;
        readonly double _windowSeconds;
        readonly int _maxFrames;

        struct EventEntry
        {
            public double T;
            public string Line;
        }

        public SessionBuffer(double windowSeconds, int maxFrames)
        {
            _windowSeconds = windowSeconds;
            _maxFrames = maxFrames;
            _frames = new Queue<CapturedFrame>(maxFrames + 4);
        }

        public void AddEvent(double t, string jsonLine)
        {
            lock (_gate)
            {
                _events.Enqueue(new EventEntry { T = t, Line = jsonLine });
                while (_events.Count > 0 &&
                       (_events.Count > MaxEvents || t - _events.Peek().T > _windowSeconds))
                {
                    _events.Dequeue();
                }
            }
        }

        public void AddFrame(CapturedFrame frame)
        {
            lock (_gate)
            {
                _frames.Enqueue(frame);
                while (_frames.Count > _maxFrames) _frames.Dequeue();
            }
        }

        /// <summary>Copies the current buffer contents (cheap: references only).</summary>
        public void Snapshot(List<string> eventLines, List<CapturedFrame> frames)
        {
            lock (_gate)
            {
                foreach (var e in _events) eventLines.Add(e.Line);
                foreach (var f in _frames) frames.Add(f);
            }
        }

        /// <summary>
        /// Moves the current buffer contents out and empties the buffer. Used by the
        /// streaming sink so each chunk contains only data since the previous chunk.
        /// </summary>
        public void Drain(List<string> eventLines, List<CapturedFrame> frames)
        {
            lock (_gate)
            {
                while (_events.Count > 0) eventLines.Add(_events.Dequeue().Line);
                while (_frames.Count > 0) frames.Add(_frames.Dequeue());
            }
        }
    }
}
