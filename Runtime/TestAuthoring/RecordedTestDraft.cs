using System;
using System.Collections.Generic;

namespace QamelCapture.TestAuthoring
{
    internal enum TestEvidenceCoverage
    {
        Complete,
        Truncated,
        Unknown,
    }

    internal sealed class TestEvidenceClip
    {
        readonly byte[] _bundleBytes;

        public TestEvidenceClip(
            byte[] bundleBytes,
            double durationSeconds,
            float captureFps,
            int width,
            int height,
            int frameCount,
            TestEvidenceCoverage coverage = TestEvidenceCoverage.Complete)
        {
            if (bundleBytes == null || bundleBytes.Length == 0)
                throw new ArgumentException("Evidence frame bundle bytes are required.", nameof(bundleBytes));
            if (durationSeconds <= 0 || double.IsNaN(durationSeconds) ||
                double.IsInfinity(durationSeconds))
                throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            if (captureFps <= 0 || float.IsNaN(captureFps) || float.IsInfinity(captureFps))
                throw new ArgumentOutOfRangeException(nameof(captureFps));
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (frameCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(frameCount));
            _bundleBytes = (byte[])bundleBytes.Clone();
            DurationSeconds = durationSeconds;
            CaptureFps = captureFps;
            Width = width;
            Height = height;
            FrameCount = frameCount;
            Coverage = coverage;
        }

        public double DurationSeconds { get; }
        public float CaptureFps { get; }
        public int Width { get; }
        public int Height { get; }
        public int FrameCount { get; }
        public TestEvidenceCoverage Coverage { get; }
        public int ByteCount => _bundleBytes.Length;
        public byte[] GetBytesCopy() => (byte[])_bundleBytes.Clone();
    }

    internal sealed class TestEvidenceRange
    {
        public TestEvidenceRange(bool hasSessionTimes, double startSessionTime, double endSessionTime)
        {
            HasSessionTimes = hasSessionTimes;
            StartSessionTime = startSessionTime;
            EndSessionTime = endSessionTime;
        }

        public bool HasSessionTimes { get; }
        public double StartSessionTime { get; }
        public double EndSessionTime { get; }
        public double DurationSeconds => HasSessionTimes
            ? Math.Max(0, EndSessionTime - StartSessionTime)
            : 0;
    }

    /// <summary>
    /// One session-only draft. Its state anchor and serialized input snapshot are
    /// immutable and are never written or uploaded by this model.
    /// </summary>
    internal sealed class RecordedTestDraft
    {
        readonly List<LocalTestRun> _validationHistory = new List<LocalTestRun>();

        public RecordedTestDraft(
            string name,
            string expectedOutcome,
            TestStateAnchor anchor,
            TestInputTraceSnapshot inputTrace,
            TestEvidenceRange demonstrationEvidence,
            TestExpectedOutcomeObservation expectedObservation = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A draft name is required.", nameof(name));
            if (string.IsNullOrWhiteSpace(expectedOutcome))
                throw new ArgumentException("An expected outcome is required.", nameof(expectedOutcome));
            Anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
            InputTrace = inputTrace ?? throw new ArgumentNullException(nameof(inputTrace));
            DemonstrationEvidence = demonstrationEvidence ??
                throw new ArgumentNullException(nameof(demonstrationEvidence));
            DraftId = Guid.NewGuid().ToString("N");
            CreatedAtUtc = DateTime.UtcNow;
            Name = name.Trim();
            ExpectedOutcome = expectedOutcome.Trim();
            ExpectedObservation = expectedObservation;
        }

        public string DraftId { get; }
        public DateTime CreatedAtUtc { get; }
        public string Name { get; private set; }
        public string ExpectedOutcome { get; private set; }
        public TestStateAnchor Anchor { get; }
        public TestInputTraceSnapshot InputTrace { get; }
        public TestEvidenceRange DemonstrationEvidence { get; }
        public TestEvidenceClip ReferenceEvidenceClip { get; private set; }
        public TestExpectedOutcomeObservation ExpectedObservation { get; }
        public IReadOnlyList<LocalTestRun> ValidationHistory =>
            _validationHistory.AsReadOnly();

        internal LocalTestRun AddRun()
        {
            var run = new LocalTestRun(_validationHistory.Count + 1);
            _validationHistory.Add(run);
            return run;
        }

        internal void ReviseMetadata(string name, string expectedOutcome)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A draft name is required.", nameof(name));
            if (string.IsNullOrWhiteSpace(expectedOutcome))
                throw new ArgumentException("An expected outcome is required.", nameof(expectedOutcome));
            Name = name.Trim();
            ExpectedOutcome = expectedOutcome.Trim();
        }

        internal void AttachReferenceEvidenceClip(TestEvidenceClip clip)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (ReferenceEvidenceClip != null)
                throw new InvalidOperationException("Reference evidence is already attached.");
            ReferenceEvidenceClip = clip;
        }
    }
}
