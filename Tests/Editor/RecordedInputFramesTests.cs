using System;
using System.IO;
using NUnit.Framework;
using QamelCapture.TestAuthoring;

namespace QamelCapture.Tests
{
    public sealed class RecordedInputFramesTests
    {
        static RecordedInputFrames.Timing Frame(float delta) => new RecordedInputFrames.Timing
        {
            DeltaTime = delta, TimeScale = 1, FixedDeltaTime = 0.02f
        };

        [Test]
        public void TimingRoundTripsWithoutChangingNativeTraceBytes()
        {
            var native = new byte[] { 1, 2, 3, 4, 5 };
            var encoded = RecordedInputFrames.Write(native, new[] { Frame(0.01f), Frame(0.075f) });
            var decoded = RecordedInputFrames.Read(encoded, out var trace);
            CollectionAssert.AreEqual(native, trace);
            Assert.AreEqual(2, decoded.Length);
            Assert.AreEqual(0.01f, decoded[0].DeltaTime);
            Assert.AreEqual(0.075f, decoded[1].DeltaTime);
            Assert.AreEqual(1f, decoded[1].TimeScale);
            Assert.AreEqual(0.02f, decoded[1].FixedDeltaTime);
            Assert.IsNull(RecordedInputFrames.Read(native, out trace));
            CollectionAssert.AreEqual(native, trace);
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void InvalidTimingCannotBeSaved(float delta)
        {
            Assert.Throws<InvalidDataException>(() =>
                RecordedInputFrames.Write(new byte[] { 1 }, new[] { Frame(delta) }));
        }

        [Test]
        public void CorruptVersionCountsLengthsAndTimingAreRejected()
        {
            var valid = RecordedInputFrames.Write(new byte[] { 1 }, new[] { Frame(0.02f) });
            foreach (var offset in new[] { 4, 8, 24 })
            {
                var corrupt = (byte[])valid.Clone();
                Array.Copy(BitConverter.GetBytes(int.MaxValue), 0, corrupt, offset, 4);
                Assert.Throws<InvalidDataException>(() => RecordedInputFrames.Read(corrupt, out _));
            }
            var invalidTiming = (byte[])valid.Clone();
            Array.Copy(BitConverter.GetBytes(float.NaN), 0, invalidTiming, 12, 4);
            Assert.Throws<InvalidDataException>(() => RecordedInputFrames.Read(invalidTiming, out _));
            var truncated = new byte[valid.Length - 1];
            Array.Copy(valid, truncated, truncated.Length);
            Assert.Throws<InvalidDataException>(() => RecordedInputFrames.Read(truncated, out _));
        }
    }
}
