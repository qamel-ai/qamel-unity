using System;

namespace QamelCapture.TestAuthoring
{
    internal enum LocalTestRunStatus
    {
        Restoring,
        Settling,
        Countdown,
        Replaying,
        AwaitingReview,
        Pass,
        Fail,
        NeedsReview,
        Error,
        Cancelled
    }

    internal enum LocalTestStageStatus
    {
        Pending,
        Running,
        Succeeded,
        Pass,
        Fail,
        NeedsReview,
        Error,
        Cancelled
    }

    internal enum TestHumanVerdict
    {
        Worked,
        DidNotWork,
        CouldNotTell
    }

    internal enum TestOutcomeErrorCode
    {
        None,
        MissingProvider,
        IncompatibleProvider,
        ProviderError,
        InvalidObservation
    }

    /// <summary>
    /// One in-memory validation attempt for a recorded draft. Infrastructure
    /// outcomes remain separate from the human gameplay verdict.
    /// </summary>
    internal sealed class LocalTestRun
    {
        public LocalTestRun(int attemptNumber)
        {
            if (attemptNumber <= 0)
                throw new ArgumentOutOfRangeException(nameof(attemptNumber));

            AttemptNumber = attemptNumber;
            RunId = Guid.NewGuid().ToString("N");
            StartedAtUtc = DateTime.UtcNow;
            Status = LocalTestRunStatus.Restoring;
            StateStageStatus = LocalTestStageStatus.Running;
            ActionStageStatus = LocalTestStageStatus.Pending;
            CheckStageStatus = LocalTestStageStatus.Pending;
            AdapterDiagnostics = TestInputReplayDiagnostics.Empty();
        }

        public string RunId { get; }
        public int AttemptNumber { get; }
        public DateTime StartedAtUtc { get; }
        public DateTime? CompletedAtUtc { get; internal set; }
        public LocalTestRunStatus Status { get; internal set; }
        public LocalTestStageStatus StateStageStatus { get; internal set; }
        public LocalTestStageStatus ActionStageStatus { get; internal set; }
        public LocalTestStageStatus CheckStageStatus { get; internal set; }
        public float StateDurationSeconds { get; internal set; }
        public float SettleDurationSeconds { get; internal set; }
        public float CountdownDurationSeconds { get; internal set; }
        public float ActionDurationSeconds { get; internal set; }
        public float TotalDurationSeconds { get; internal set; }
        public TestStateErrorCode StateErrorCode { get; internal set; }
        public TestInputTraceErrorCode InputErrorCode { get; internal set; }
        public TestOutcomeErrorCode OutcomeErrorCode { get; internal set; }
        public string Error { get; internal set; }
        public string ReviewNote { get; internal set; }
        public TestEvidenceRange ReplayEvidence { get; internal set; }
        public TestEvidenceClip ReplayEvidenceClip { get; internal set; }
        public TestInputReplayDiagnostics AdapterDiagnostics { get; internal set; }
        public TestOutcomeComparison OutcomeComparison { get; internal set; }

        public bool IsInfrastructureFailure => Status == LocalTestRunStatus.Error;
        public bool IsTerminal => Status == LocalTestRunStatus.Pass ||
                                  Status == LocalTestRunStatus.Fail ||
                                  Status == LocalTestRunStatus.NeedsReview ||
                                  Status == LocalTestRunStatus.Error ||
                                  Status == LocalTestRunStatus.Cancelled;
    }
}
