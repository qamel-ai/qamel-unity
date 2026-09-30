using System;

namespace QamelCapture.TestAuthoring
{
    /// <summary>
    /// Reads the existing capture session clock when one is active. It records
    /// local range metadata only and never emits to the capture stream.
    /// </summary>
    internal sealed class TestEvidenceBoundary
    {
        readonly Func<double?> _sessionNow;

        public TestEvidenceBoundary(Func<double?> sessionNow)
        {
            _sessionNow = sessionNow;
        }

        public bool TryReadSessionTime(out double sessionTime)
        {
            sessionTime = 0;
            if (_sessionNow == null)
                return false;

            var value = _sessionNow();
            if (!value.HasValue || value.Value < 0 || double.IsNaN(value.Value) ||
                double.IsInfinity(value.Value))
                return false;

            sessionTime = value.Value;
            return true;
        }

        public static TestEvidenceBoundary CurrentCaptureSession()
        {
            return new TestEvidenceBoundary(() =>
            {
                var runner = QamelRunner.Instance;
                return runner != null && runner.HasActiveSession
                    ? (double?)runner.Now
                    : null;
            });
        }
    }
}
