using NUnit.Framework;
using QamelCapture;

namespace QamelCapture.Tests
{
    public class CaptureHealthCountersTests
    {
        [Test]
        public void SnapshotStartsAtZero()
        {
            var health = new CaptureHealthCounters().Snapshot();
            Assert.AreEqual(0, health.Attempted);
            Assert.AreEqual(0, health.Kept);
            Assert.AreEqual(0, health.DropInflight);
            Assert.AreEqual(0, health.DropEncodeQueue);
            Assert.AreEqual(0, health.ReadbackErrors);
            Assert.AreEqual(0, health.EncodeErrors);
        }

        [Test]
        public void CountersAccumulateIndependently()
        {
            var counters = new CaptureHealthCounters();
            counters.OnAttempt();
            counters.OnAttempt();
            counters.OnAttempt();
            counters.OnKept();
            counters.OnKept();
            counters.OnDropInflight();
            counters.OnDropEncodeQueue();
            counters.OnDropEncodeQueue();
            counters.OnReadbackError();
            counters.OnEncodeError();
            counters.OnEncodeError();
            counters.OnEncodeError();

            var snap = counters.Snapshot();
            Assert.AreEqual(3, snap.Attempted);
            Assert.AreEqual(2, snap.Kept);
            Assert.AreEqual(1, snap.DropInflight);
            Assert.AreEqual(2, snap.DropEncodeQueue);
            Assert.AreEqual(1, snap.ReadbackErrors);
            Assert.AreEqual(3, snap.EncodeErrors);
        }

        [Test]
        public void ManifestCarriesNonZeroCaptureHealth()
        {
            string json = ReportManifest.Build(new ReportManifestData
            {
                SessionId = "s",
                ReportId = "r",
                SessionStartedUtc = System.DateTime.UtcNow,
                CaptureHealth = new CaptureHealthSnapshot
                {
                    Attempted = 100,
                    Kept = 90,
                    DropInflight = 7,
                    DropEncodeQueue = 2,
                    ReadbackErrors = 1,
                    EncodeErrors = 0,
                },
            });

            var parsed = TestJson.Parse(json);
            Assert.AreEqual("100", parsed["capture_attempted"]);
            Assert.AreEqual("90", parsed["capture_kept"]);
            Assert.AreEqual("7", parsed["capture_drop_inflight"]);
            Assert.AreEqual("2", parsed["capture_drop_encode"]);
            Assert.AreEqual("1", parsed["capture_readback_errors"]);
            Assert.AreEqual("0", parsed["capture_encode_errors"]);
        }
    }
}
