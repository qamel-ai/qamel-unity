using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using QamelCapture;

namespace QamelCapture.Tests
{
    public class SessionBufferTests
    {
        static List<string> Events(SessionBuffer buffer)
        {
            var events = new List<string>();
            buffer.Snapshot(events, new List<CapturedFrame>());
            return events;
        }

        static List<CapturedFrame> Frames(SessionBuffer buffer)
        {
            var frames = new List<CapturedFrame>();
            buffer.Snapshot(new List<string>(), frames);
            return frames;
        }

        static CapturedFrame Frame(long index) =>
            new CapturedFrame { Index = index, T = index, Width = 2, Height = 2, Jpg = new byte[] { 1 } };

        [Test]
        public void KeepsEventsInsideTheTimeWindow()
        {
            var buffer = new SessionBuffer(windowSeconds: 10, maxFrames: 5);
            buffer.AddEvent(0, "e0");
            buffer.AddEvent(4, "e4");
            buffer.AddEvent(9, "e9");
            Assert.AreEqual(new[] { "e0", "e4", "e9" }, Events(buffer));
        }

        [Test]
        public void EvictsEventsOlderThanTheWindow()
        {
            var buffer = new SessionBuffer(windowSeconds: 10, maxFrames: 5);
            buffer.AddEvent(0, "e0");
            buffer.AddEvent(4, "e4");
            buffer.AddEvent(15, "e15"); // e0 is now 15s old, e4 is 11s old
            Assert.AreEqual(new[] { "e15" }, Events(buffer));
        }

        [Test]
        public void EvictsOldestFramesBeyondCapacity()
        {
            var buffer = new SessionBuffer(windowSeconds: 999, maxFrames: 3);
            for (long i = 0; i < 5; i++) buffer.AddFrame(Frame(i));
            var frames = Frames(buffer);
            Assert.AreEqual(3, frames.Count);
            Assert.AreEqual(2, frames[0].Index);
            Assert.AreEqual(4, frames[2].Index);
        }

        [Test]
        public void SnapshotAppendsWithoutClearingAndPreservesOrder()
        {
            var buffer = new SessionBuffer(windowSeconds: 100, maxFrames: 5);
            buffer.AddEvent(1, "a");
            buffer.AddEvent(2, "b");

            var target = new List<string> { "existing" };
            buffer.Snapshot(target, new List<CapturedFrame>());
            Assert.AreEqual(new[] { "existing", "a", "b" }, target);

            // The buffer itself is untouched by snapshotting.
            Assert.AreEqual(new[] { "a", "b" }, Events(buffer));
        }

        [Test]
        public void DrainEmptiesTheBufferAndPreservesOrder()
        {
            var buffer = new SessionBuffer(windowSeconds: 100, maxFrames: 5);
            buffer.AddEvent(1, "a");
            buffer.AddEvent(2, "b");
            buffer.AddFrame(Frame(0));

            var events = new List<string>();
            var frames = new List<CapturedFrame>();
            buffer.Drain(events, frames);
            Assert.AreEqual(new[] { "a", "b" }, events);
            Assert.AreEqual(1, frames.Count);

            // Second drain yields nothing: the chunk streamer relies on this.
            events.Clear();
            frames.Clear();
            buffer.Drain(events, frames);
            Assert.AreEqual(0, events.Count);
            Assert.AreEqual(0, frames.Count);
        }

        [Test]
        public void ConcurrentWritersDoNotCorruptTheBuffer()
        {
            var buffer = new SessionBuffer(windowSeconds: 1000, maxFrames: 64);
            const int perThread = 2000;
            var threads = new Thread[4];
            for (int threadIndex = 0; threadIndex < threads.Length; threadIndex++)
            {
                threads[threadIndex] = new Thread(() =>
                {
                    for (int i = 0; i < perThread; i++)
                    {
                        buffer.AddEvent(i * 0.001, "event");
                        if ((i & 31) == 0) buffer.AddFrame(Frame(i));
                    }
                });
            }
            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) thread.Join();

            Assert.AreEqual(threads.Length * perThread, Events(buffer).Count);
            Assert.AreEqual(64, Frames(buffer).Count);
        }
    }
}
