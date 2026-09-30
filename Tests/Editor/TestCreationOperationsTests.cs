using System;
using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;

namespace QamelCapture.Tests
{
    public sealed class TestCreationOperationsTests
    {
        const string Installation = "018f0000-0000-7000-8000-000000000001";
        const string Session = "018f0000-0000-7000-8000-000000000002";
        const string TestId = "018f0000-0000-7000-8000-000000000003";
        const string VersionId = "018f0000-0000-7000-8000-000000000004";

        [Test]
        public void BuildsAnExactPollForTheCurrentInstallationAndSession()
        {
            string json = TestCreationProtocol.PollRequest(Installation, Session);
            StringAssert.Contains("\"contractVersion\":1", json);
            StringAssert.Contains("\"installationId\":\"" + Installation + "\"", json);
            StringAssert.Contains("\"sessionId\":\"" + Session + "\"", json);
            Assert.AreEqual(
                "{\"contractVersion\":1,\"installationId\":\"" + Installation +
                "\",\"sessionId\":null}",
                TestCreationProtocol.PollRequest(Installation, null));
            Assert.Throws<ArgumentException>(() =>
                TestCreationProtocol.PollRequest(Guid.Empty.ToString(), null));
        }

        [Test]
        public void BuildsOnlyTheClosedCreationTransitions()
        {
            StringAssert.Contains("\"nextStatus\":\"preparing\"",
                TestCreationProtocol.PreparingRequest(
                    Installation, Session, "Assets/Scenes/SampleScene.unity"));
            StringAssert.Contains("\"nextStatus\":\"recording\"",
                TestCreationProtocol.RecordingRequest(Installation, Session));
            StringAssert.Contains("\"nextStatus\":\"draft_ready\"",
                TestCreationProtocol.DraftReadyRequest(
                    Installation, Session, new string('a', 32), 8 * 60 * 60, 42,
                    new[] { "Keyboard", "Mouse" }));
            StringAssert.Contains("\"nextStatus\":\"saving\"",
                TestCreationProtocol.SavingRequest(Installation, Session));
            StringAssert.Contains("\"nextStatus\":\"saved\"",
                TestCreationProtocol.SavedRequest(Installation, Session, TestId, VersionId));
            StringAssert.Contains("\"nextStatus\":\"failed\"",
                TestCreationProtocol.FailedRequest(
                    Installation, Session, "recording", "input_failed", "Input stopped."));
            Assert.Throws<ArgumentException>(() => TestCreationProtocol.FailedRequest(
                Installation, Session, "saved", "invalid", "Invalid transition."));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TestCreationProtocol.DraftReadyRequest(
                    Installation, Session, new string('a', 32), 1, 1000001,
                    Array.Empty<string>()));
        }

        [Test]
        public void ReadsRecordingAndReviewedDraftStates()
        {
            const string recording = "{\"contractVersion\":1,\"hasSession\":true,\"session\":{" +
                "\"id\":\"" + Session + "\",\"status\":\"recording\"," +
                "\"activeScenePath\":\"Assets/Scenes/SampleScene.unity\"}}";
            Assert.IsTrue(TestCreationProtocol.TryReadPoll(
                recording, out var recordingSession, out var recordingError), recordingError);
            Assert.AreEqual("recording", recordingSession.Status);

            string draft = "{\"contractVersion\":1,\"hasSession\":true,\"session\":{" +
                "\"id\":\"" + Session + "\",\"status\":\"draft_ready\"," +
                "\"activeScenePath\":\"Assets/Scenes/SampleScene.unity\"," +
                "\"localDraftId\":\"" + new string('b', 32) + "\"," +
                "\"recordedDurationSeconds\":3.25," +
                "\"replayableInputEventCount\":14," +
                "\"deviceLayouts\":[\"Keyboard\",\"Mouse\"]}}";
            Assert.IsTrue(TestCreationProtocol.TryReadPoll(
                draft, out var draftSession, out var draftError), draftError);
            Assert.AreEqual(14, draftSession.ReplayableInputEventCount);
            Assert.AreEqual(2, draftSession.DeviceLayouts.Length);
        }

        [Test]
        public void RejectsIncompleteOrUnknownServerState()
        {
            Assert.IsFalse(TestCreationProtocol.TryReadPoll(
                "{\"contractVersion\":1,\"hasSession\":true,\"session\":{" +
                "\"id\":\"" + Session + "\",\"status\":\"recording\"}}",
                out _, out _));
            Assert.IsFalse(TestCreationProtocol.TryReadPoll(
                "{\"contractVersion\":1,\"hasSession\":true,\"session\":{" +
                "\"id\":\"" + Session + "\",\"status\":\"running\"}}",
                out _, out _));
            Assert.IsFalse(TestCreationProtocol.TryReadAdvance(
                "{\"contractVersion\":1,\"sessionId\":\"" + Session +
                "\",\"status\":\"saved\"}", Session, "preparing", out _));
        }

        [Test]
        public void AcceptsNoCurrentCreationSession()
        {
            Assert.IsTrue(TestCreationProtocol.TryReadPoll(
                "{\"contractVersion\":1,\"hasSession\":false,\"session\":null}",
                out var session, out var error), error);
            Assert.IsNull(session);
        }

        [Test]
        public void UsesARealIdleAndActivePollCadence()
        {
            Assert.AreEqual(5, TestCreationPollingPolicy.DelayAfterPoll(null));
            Assert.AreEqual(2, TestCreationPollingPolicy.DelayAfterPoll(
                new ConnectedTestCreation { Status = "recording" }));
            Assert.AreEqual(5, TestCreationPollingPolicy.DelayAfterPoll(
                new ConnectedTestCreation { Status = "saved" }));
            Assert.AreEqual(5, TestCreationPollingPolicy.RetryIntervalSeconds);
        }

        [Test]
        public void UsesDedicatedCreationEndpoints()
        {
            Assert.AreEqual("https://qamel.ai/api/v1/test-creation/poll",
                TestDefinitionRoutes.TestCreationPollUrl("https://qamel.ai"));
            Assert.AreEqual("https://qamel.ai/api/v1/test-creation/advance",
                TestDefinitionRoutes.TestCreationAdvanceUrl("https://qamel.ai"));
        }
    }
}
