using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;
using QamelCapture.TestAuthoring;

namespace QamelCapture.Tests
{
    public sealed class ConnectedTestRunnerOperationsTests
    {
        const string InstallationId = "018f0000-0000-7000-8000-000000000301";
        const string RunnerId = "018f0000-0000-7000-8000-000000000302";
        const string TestRunId = "018f0000-0000-7000-8000-000000000303";
        const string TestVersionId = "018f0000-0000-7000-8000-000000000304";

        sealed class OutcomeProvider : IQamelTestStateProvider, IQamelTestOutcomeProvider
        {
            public string ProviderId => "qamel.fixture.state";
            public int StateFormatVersion => 2;
            public int OutcomeFormatVersion => 3;

            public void CaptureState(TestStateCaptureContext context)
            {
                context.Fail("not used");
            }

            public void RestoreState(TestStateRestoreContext context)
            {
                context.ReadyForReplay();
            }

            public TestOutcomeObservation ObserveOutcome()
            {
                return new TestOutcomeObservation("door=open", "Door is open");
            }
        }

        sealed class StateProvider : IQamelTestStateProvider
        {
            public string ProviderId => "qamel.unity.scene_reload";
            public int StateFormatVersion => 1;

            public void CaptureState(TestStateCaptureContext context)
            {
                context.Fail("not used");
            }

            public void RestoreState(TestStateRestoreContext context)
            {
                context.ReadyForReplay();
            }
        }

        [Test]
        public void BuildsTheConcreteAutomaticUnityRunnerDescriptor()
        {
            var registry = new TestStateRegistry();
            Assert.IsTrue(registry.Register(new OutcomeProvider()).Succeeded);
            var environment = new TestRunExecutionEnvironment(
                "6000.0",
                "1.14.2",
                "OSXEditor",
                "Assets/Fixture.unity",
                "0.1.0");

            Assert.IsTrue(TestRunnerLeaseOperation.TryBuildRequest(
                InstallationId,
                "Fixture project",
                registry,
                environment,
                out var json,
                out var error), error);

            StringAssert.Contains("\"contractVersion\":1", json);
            StringAssert.Contains("\"installationId\":\"" + InstallationId + "\"", json);
            StringAssert.Contains("\"stateProviderId\":\"qamel.fixture.state\"", json);
            StringAssert.Contains("\"outcomeFormatVersion\":3", json);
            StringAssert.Contains(
                "\"qamel.unity.compare_outcome_observation\"",
                json);
            StringAssert.DoesNotContain("qamel.human.review_expected_outcome", json);
        }

        [Test]
        public void BuildsAGenericHumanReviewRunnerDescriptor()
        {
            var registry = new TestStateRegistry();
            registry.RegisterFallback(new StateProvider());
            var environment = new TestRunExecutionEnvironment(
                "6000.0",
                "1.14.2",
                "OSXEditor",
                "Assets/Fixture.unity",
                "0.1.0");

            Assert.IsTrue(TestRunnerLeaseOperation.TryBuildRequest(
                InstallationId,
                "Fixture project",
                registry,
                environment,
                out var json,
                out var error), error);
            StringAssert.Contains("\"stateProviderId\":\"qamel.unity.scene_reload\"", json);
            StringAssert.Contains("qamel.human.review_expected_outcome", json);
            StringAssert.DoesNotContain("outcomeFormatVersion", json);
            StringAssert.DoesNotContain("qamel.unity.compare_outcome_observation", json);
        }

        [Test]
        public void ParsesNoWorkAndAssignedRunResponses()
        {
            const string idleJson = "{" +
                "\"contractVersion\":1," +
                "\"runnerId\":\"" + RunnerId + "\"," +
                "\"run\":null}";
            Assert.IsTrue(TestRunnerLeaseOperation.TryReadLease(
                idleJson,
                out var idle,
                out var idleError), idleError);
            Assert.IsFalse(idle.HasRun);

            const string assignedJson = "{" +
                "\"contractVersion\":1," +
                "\"runnerId\":\"" + RunnerId + "\"," +
                "\"run\":{" +
                "\"testRunId\":\"" + TestRunId + "\"," +
                "\"testVersionId\":\"" + TestVersionId + "\"," +
                "\"leaseExpiresAt\":\"2026-09-07T05:00:30+00:00\"}}";
            Assert.IsTrue(TestRunnerLeaseOperation.TryReadLease(
                assignedJson,
                out var assigned,
                out var assignedError), assignedError);
            Assert.IsTrue(assigned.HasRun);
            Assert.AreEqual(TestRunId, assigned.TestRunId);
            Assert.AreEqual(TestVersionId, assigned.TestVersionId);
        }

        [Test]
        public void ValidatesTheCompletedRunIdentityAndStatus()
        {
            const string json = "{" +
                "\"contractVersion\":1," +
                "\"testRunId\":\"" + TestRunId + "\"," +
                "\"status\":\"passed\"}";

            Assert.IsTrue(TestRunResultUploadOperation.TryReadCompletion(
                json,
                TestRunId,
                out var status,
                out var error), error);
            Assert.AreEqual("passed", status);
            Assert.IsFalse(TestRunResultUploadOperation.TryReadCompletion(
                json,
                RunnerId,
                out _,
                out _));
        }

        [Test]
        public void ParsesActiveAndCancelledRunHeartbeats()
        {
            const string activeJson = "{" +
                "\"contractVersion\":1," +
                "\"testRunId\":\"" + TestRunId + "\"," +
                "\"status\":\"running\"," +
                "\"leaseExpiresAt\":\"2026-09-07T05:00:30+00:00\"}";
            Assert.IsTrue(TestRunHeartbeatOperation.TryReadHeartbeat(
                activeJson,
                TestRunId,
                out var activeStatus,
                out var leaseExpiresAt,
                out var activeError), activeError);
            Assert.AreEqual("running", activeStatus);
            Assert.IsTrue(leaseExpiresAt.HasValue);

            const string cancelledJson = "{" +
                "\"contractVersion\":1," +
                "\"testRunId\":\"" + TestRunId + "\"," +
                "\"status\":\"cancelled\"," +
                "\"leaseExpiresAt\":null}";
            Assert.IsTrue(TestRunHeartbeatOperation.TryReadHeartbeat(
                cancelledJson,
                TestRunId,
                out var cancelledStatus,
                out var cancelledLease,
                out var cancelledError), cancelledError);
            Assert.AreEqual("cancelled", cancelledStatus);
            Assert.IsFalse(cancelledLease.HasValue);
        }

        [Test]
        public void BuildsValidatedRunServiceRoutes()
        {
            Assert.AreEqual(
                "https://qamel.ai/api/v1/runners/lease",
                TestDefinitionRoutes.RunnerLeaseUrl("https://qamel.ai/"));
            Assert.IsTrue(TestDefinitionRoutes.TryGetTestRunResultUrl(
                "http://localhost:3000",
                TestRunId,
                out var url));
            Assert.AreEqual(
                "http://localhost:3000/api/v1/test-runs/" + TestRunId + "/result",
                url);
            Assert.IsTrue(TestDefinitionRoutes.TryGetTestRunHeartbeatUrl(
                "http://localhost:3000",
                TestRunId,
                out var heartbeatUrl));
            Assert.AreEqual(
                "http://localhost:3000/api/v1/test-runs/" + TestRunId +
                "/heartbeat",
                heartbeatUrl);
        }

        [TestCase((int)TestAuthoringControllerState.Idle, false, false, true)]
        [TestCase((int)TestAuthoringControllerState.DraftReady, true, true, true)]
        [TestCase((int)TestAuthoringControllerState.ReplayError, true, true, true)]
        [TestCase((int)TestAuthoringControllerState.ReplayCancelled, true, true, true)]
        [TestCase((int)TestAuthoringControllerState.DraftReady, true, false, false)]
        [TestCase((int)TestAuthoringControllerState.Recording, false, false, false)]
        [TestCase((int)TestAuthoringControllerState.NeedsReview, true, true, false)]
        public void PollsWhileASavedDraftCanSafelyYieldToAConnectedRun(
            int stateValue,
            bool hasCurrentDraft,
            bool currentDraftIsDurable,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                ConnectedTestRunnerReadiness.CanYieldLocalController(
                    (TestAuthoringControllerState)stateValue,
                    hasCurrentDraft,
                    currentDraftIsDurable));
        }
    }
}
