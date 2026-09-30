using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;
using QamelCapture.TestAuthoring;

namespace QamelCapture.Tests
{
    public sealed class TestRunResultSerializerTests
    {
        const string SetupStageId = "018f0000-0000-7000-8000-000000000201";
        const string ActionStageId = "018f0000-0000-7000-8000-000000000202";
        const string CheckStageId = "018f0000-0000-7000-8000-000000000203";

        [Test]
        public void SerializesAutomaticPassAgainstExactImmutableStages()
        {
            var run = new LocalTestRun(1)
            {
                Status = LocalTestRunStatus.Pass,
                StateStageStatus = LocalTestStageStatus.Succeeded,
                ActionStageStatus = LocalTestStageStatus.Succeeded,
                CheckStageStatus = LocalTestStageStatus.Pass,
                StateDurationSeconds = 0.25f,
                SettleDurationSeconds = 0.05f,
                ActionDurationSeconds = 1.5f,
                AdapterDiagnostics = new TestInputReplayDiagnostics(
                    "Recorded timing", 42, 0, new[] { "Keyboard" }),
                OutcomeComparison = new TestOutcomeComparison(
                    new TestOutcomeObservation("door=open", "Door is open"),
                    new TestOutcomeObservation("door=open", "Door is open")),
            };

            string json = TestRunResultSerializer.Serialize(
                Version(), run, Environment());

            StringAssert.Contains("\"contractVersion\":1", json);
            StringAssert.Contains("\"stageId\":\"" + SetupStageId + "\"", json);
            StringAssert.Contains("\"stageId\":\"" + ActionStageId + "\"", json);
            StringAssert.Contains("\"stageId\":\"" + CheckStageId + "\"", json);
            StringAssert.Contains("\"durationMs\":300", json);
            StringAssert.Contains("\"durationMs\":1500", json);
            StringAssert.Contains("\"status\":\"passed\"", json);
            StringAssert.Contains("\"expectedValue\":\"door=open\"", json);
            StringAssert.Contains("\"observedValue\":\"door=open\"", json);
            StringAssert.Contains("\"contaminated\":false", json);
        }

        [Test]
        public void KeepsProviderErrorsSeparateFromGameplayFailures()
        {
            var run = new LocalTestRun(1)
            {
                Status = LocalTestRunStatus.Error,
                StateStageStatus = LocalTestStageStatus.Succeeded,
                ActionStageStatus = LocalTestStageStatus.Succeeded,
                CheckStageStatus = LocalTestStageStatus.Error,
                OutcomeErrorCode = TestOutcomeErrorCode.ProviderError,
                Error = "Outcome provider could not read the door.",
            };

            string json = TestRunResultSerializer.Serialize(
                Version(), run, Environment());

            StringAssert.Contains("\"status\":\"error\"", json);
            StringAssert.Contains("\"code\":\"outcome_provider_error\"", json);
            StringAssert.Contains("Outcome provider could not read the door.", json);
            StringAssert.DoesNotContain("\"status\":\"failed\"", json);
        }

        [Test]
        public void SerializesGenericReplayAsNeedsReviewWithItsReason()
        {
            var run = new LocalTestRun(1)
            {
                Status = LocalTestRunStatus.NeedsReview,
                StateStageStatus = LocalTestStageStatus.Succeeded,
                ActionStageStatus = LocalTestStageStatus.Succeeded,
                CheckStageStatus = LocalTestStageStatus.NeedsReview,
                ReviewNote =
                    "No automatic outcome check is configured. Review the replay evidence.",
            };

            string json = TestRunResultSerializer.Serialize(
                Version(humanReview: true), run, Environment());

            StringAssert.Contains("\"status\":\"needs_review\"", json);
            StringAssert.Contains("\"reviewNote\":", json);
            StringAssert.Contains("No automatic outcome check is configured", json);
            Assert.IsTrue(TestLabSession.RunnerUsesHumanReview(Version(humanReview: true)));
            Assert.IsFalse(TestLabSession.RunnerUsesHumanReview(Version()));
        }

        [Test]
        public void RejectsAnUnfinishedLocalRun()
        {
            Assert.Throws<System.ArgumentException>(() =>
                TestRunResultSerializer.Serialize(
                    Version(), new LocalTestRun(1), Environment()));
        }

        static SavedTestVersion Version(bool humanReview = false)
        {
            return new SavedTestVersion(
                "018f0000-0000-7000-8000-000000000210",
                "Open the door",
                "validating",
                "018f0000-0000-7000-8000-000000000211",
                1,
                3,
                new string('a', 64),
                "2026-09-07T00:00:00Z",
                true,
                null,
                null,
                new[]
                {
                    new SavedTestStage(
                        SetupStageId,
                        1,
                        "setup",
                        "scripted",
                        SavedTestReplayCompatibility.RestoreStateAdapter,
                        null,
                        new SavedTestStageConfiguration()),
                    new SavedTestStage(
                        ActionStageId,
                        2,
                        "action",
                        "scripted",
                        SavedTestReplayCompatibility.InputTraceAdapter,
                        "018f0000-0000-7000-8000-000000000212",
                        new SavedTestStageConfiguration()),
                    new SavedTestStage(
                        CheckStageId,
                        3,
                        "check",
                        humanReview ? "human" : "scripted",
                        humanReview
                            ? SavedTestReplayCompatibility.HumanReviewAdapter
                            : SavedTestReplayCompatibility.OutcomeObservationAdapter,
                        null,
                        new SavedTestStageConfiguration()),
                },
                null);
        }

        static TestRunExecutionEnvironment Environment()
        {
            return new TestRunExecutionEnvironment(
                "6000.0",
                "1.14.2",
                "OSXEditor",
                "Assets/Fixture.unity",
                "0.1.0");
        }
    }
}
