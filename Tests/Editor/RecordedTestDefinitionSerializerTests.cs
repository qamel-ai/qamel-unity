using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using QamelCapture.Editor.TestAuthoring;
using QamelCapture.TestAuthoring;
using UnityEngine;

namespace QamelCapture.Tests
{
    public sealed class RecordedTestDefinitionSerializerTests
    {
        [Test]
        public void SerializesReplayableVersionThreeContractAndKeepsBytesSeparate()
        {
            var stateBytes = new byte[] { 3, 5, 8 };
            var traceBytes = new byte[] { 13, 21, 34, 55 };
            var anchor = new TestStateAnchor(
                "qamel.fixture.state",
                2,
                "Fixture state",
                stateBytes,
                "FixtureScene",
                "Assets/FixtureScene.unity");
            var metrics = new TestInputTraceMetrics(
                retainedEventCount: 7,
                recordedEventCount: 7,
                retainedStateEventCount: 4,
                retainedEventBytes: traceBytes.Length,
                allocatedBytes: 128,
                maximumBytes: 1024,
                hasEventTimes: true,
                oldestEventTime: 10,
                newestEventTime: 12.5,
                deviceLayouts: new[] { "Keyboard", "Mouse" });
            var draft = new RecordedTestDraft(
                "Open \"door\"",
                "The door opens.\nThe key is consumed.",
                anchor,
                new TestInputTraceSnapshot(traceBytes, metrics),
                new TestEvidenceRange(false, 0, 0));
            AttachReferenceFrame(draft);

            var upload = RecordedTestDefinitionSerializer.Serialize(
                draft,
                new RecordedTestReplayMetadata(
                    "2022.3",
                    "1.14.2"));

            Assert.AreEqual("Open \"door\"", upload.TestName);
            CollectionAssert.AreEqual(stateBytes, upload.StatePayload);
            CollectionAssert.AreEqual(traceBytes, upload.InputTrace);
            Assert.AreEqual(stateBytes.Length, upload.StateByteCount);
            Assert.AreEqual(traceBytes.Length, upload.InputTraceByteCount);
            Assert.AreEqual(Sha256(stateBytes), upload.StateSha256);
            Assert.AreEqual(Sha256(traceBytes), upload.InputTraceSha256);
            Assert.AreEqual(3, upload.ContractVersion);
            StringAssert.Contains("\"contractVersion\":3", upload.Json);
            Assert.IsFalse(upload.Json.Contains("\"runtime\":"));
            StringAssert.Contains(
                "\"idempotencyKey\":\"" + draft.DraftId + "\"",
                upload.Json);
            StringAssert.Contains("\"status\":\"draft\"", upload.Json);
            StringAssert.Contains("\"kind\":\"state_payload\"", upload.Json);
            StringAssert.Contains("\"kind\":\"input_trace\"", upload.Json);
            StringAssert.Contains("\"startingState\":{", upload.Json);
            Assert.IsFalse(upload.Json.Contains("activeSceneName"));
            StringAssert.Contains(
                "\"activeScenePath\":\"Assets/FixtureScene.unity\"",
                upload.Json);
            StringAssert.Contains("\"executor\":\"scripted\"", upload.Json);
            StringAssert.Contains(
                "\"adapter\":\"qamel.unity.restore_starting_state\"",
                upload.Json);
            StringAssert.Contains(
                "\"adapter\":\"qamel.unity.replay_input_trace\"",
                upload.Json);
            StringAssert.Contains(
                "\"adapter\":\"qamel.human.review_expected_outcome\"",
                upload.Json);
            StringAssert.Contains("\"replayableInputEventCount\":4", upload.Json);
            StringAssert.Contains("\"recordedDurationSeconds\":2.5", upload.Json);
            StringAssert.Contains("\"unityRelease\":\"2022.3\"", upload.Json);
            StringAssert.Contains("\"inputSystemVersion\":\"1.14.2\"", upload.Json);
            StringAssert.Contains("\"deviceLayouts\":[\"Keyboard\",\"Mouse\"]", upload.Json);
            StringAssert.Contains("Open \\\"door\\\"", upload.Json);
            StringAssert.Contains(
                "\"configuration\":{\"expectedOutcome\":" +
                "\"The door opens.\\nThe key is consumed.\"}",
                upload.Json);
            StringAssert.Contains(Sha256(stateBytes), upload.Json);
            StringAssert.Contains(Sha256(traceBytes), upload.Json);

            Assert.IsFalse(upload.Json.Contains("authoringOrigin"));
            Assert.IsFalse(upload.Json.Contains("provenance"));
            Assert.IsFalse(upload.Json.Contains("compatibility"));
            Assert.IsFalse(upload.Json.Contains("validationSummary"));
            Assert.IsFalse(upload.Json.Contains("clientDraftId"));
            Assert.IsFalse(upload.Json.Contains("lifecycle"));
            Assert.IsFalse(upload.Json.Contains("testState"));
            Assert.IsFalse(upload.Json.Contains("mediaType"));
            Assert.IsFalse(upload.Json.Contains("replayMode"));
            Assert.IsFalse(upload.Json.Contains("\"order\":"));
            Assert.IsFalse(upload.Json.Contains("\"executor\":\"deterministic\""));
            Assert.IsFalse(upload.Json.Contains("qamel.unity.test_state_provider"));
            Assert.IsFalse(upload.Json.Contains("qamel.unity.input_system_trace"));
            Assert.IsFalse(upload.Json.Contains("qamel.human.expected_outcome"));
            Assert.IsFalse(upload.Json.Contains("\"eventCount\":"));
            Assert.IsFalse(upload.Json.Contains("\"durationSeconds\":"));
            Assert.IsFalse(upload.Json.Contains("\"name\":\"Restore starting state\""));
            Assert.IsFalse(upload.Json.Contains("\"name\":\"Replay recorded actions\""));
            Assert.IsFalse(upload.Json.Contains("\"name\":\"Review expected outcome\""));
            Assert.AreEqual(1, Count(upload.Json, "\"expectedOutcome\":"));

            upload.StatePayload[0] = 99;
            upload.InputTrace[0] = 99;
            CollectionAssert.AreEqual(stateBytes, anchor.GetPayloadCopy());
            CollectionAssert.AreEqual(traceBytes, draft.InputTrace.GetBytesCopy());
        }

        [Test]
        public void SerializesGameOwnedOutcomeObservationAsScriptedCheck()
        {
            var draft = new RecordedTestDraft(
                "Open door automatically",
                "The door opens.",
                new TestStateAnchor(
                    "qamel.fixture.state",
                    2,
                    "Fixture state",
                    new byte[] { 1, 2 },
                    "FixtureScene",
                    "Assets/FixtureScene.unity"),
                new TestInputTraceSnapshot(
                    new byte[] { 3, 4 },
                    new TestInputTraceMetrics(
                        1, 1, 1, 2, 2, 8, true, 0, 1, new[] { "Keyboard" })),
                new TestEvidenceRange(false, 0, 0),
                new TestExpectedOutcomeObservation(
                    "qamel.fixture.state",
                    1,
                    new TestOutcomeObservation("door=open", "Door is open")));
            AttachReferenceFrame(draft);

            var upload = RecordedTestDefinitionSerializer.Serialize(
                draft,
                new RecordedTestReplayMetadata("2022.3", "1.14.2"));

            StringAssert.Contains(
                "\"adapter\":\"qamel.unity.compare_outcome_observation\"",
                upload.Json);
            StringAssert.Contains("\"executor\":\"scripted\"", upload.Json);
            StringAssert.Contains("\"providerId\":\"qamel.fixture.state\"", upload.Json);
            StringAssert.Contains("\"outcomeFormatVersion\":1", upload.Json);
            StringAssert.Contains("\"expectedValue\":\"door=open\"", upload.Json);
            StringAssert.Contains("\"expectedSummary\":\"Door is open\"", upload.Json);
            Assert.IsFalse(upload.Json.Contains("qamel.human.review_expected_outcome"));
        }

        static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(bytes);
                var hex = new StringBuilder(digest.Length * 2);
                foreach (byte value in digest) hex.Append(value.ToString("x2"));
                return hex.ToString();
            }
        }

        static void AttachReferenceFrame(RecordedTestDraft draft)
        {
            draft.AttachReferenceEvidenceClip(new TestEvidenceClip(
                new byte[] { 0x50, 0x4b, 0x05, 0x06 },
                1,
                6,
                640,
                360,
                6));
        }

        static int Count(string value, string needle)
        {
            int count = 0;
            int offset = 0;
            while ((offset = value.IndexOf(needle, offset, System.StringComparison.Ordinal)) >= 0)
            {
                count += 1;
                offset += needle.Length;
            }
            return count;
        }
    }
}
