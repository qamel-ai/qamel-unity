using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using QamelCapture.TestAuthoring;

namespace QamelCapture.Editor.TestAuthoring
{
    internal sealed class RecordedTestDefinitionUpload
    {
        public RecordedTestDefinitionUpload(
            int contractVersion,
            string testName,
            string json,
            byte[] statePayload,
            byte[] inputTrace,
            string stateSha256,
            string inputTraceSha256,
            TestEvidenceClip referenceEvidenceClip)
        {
            if (contractVersion <= 0)
                throw new ArgumentOutOfRangeException(nameof(contractVersion));
            if (string.IsNullOrWhiteSpace(testName))
                throw new ArgumentException("A Test name is required.", nameof(testName));
            ContractVersion = contractVersion;
            TestName = testName.Trim();
            Json = json ?? throw new ArgumentNullException(nameof(json));
            StatePayload = statePayload == null
                ? throw new ArgumentNullException(nameof(statePayload))
                : (byte[])statePayload.Clone();
            InputTrace = inputTrace == null
                ? throw new ArgumentNullException(nameof(inputTrace))
                : (byte[])inputTrace.Clone();
            StateSha256 = stateSha256 ??
                throw new ArgumentNullException(nameof(stateSha256));
            InputTraceSha256 = inputTraceSha256 ??
                throw new ArgumentNullException(nameof(inputTraceSha256));
            ReferenceEvidenceClip = referenceEvidenceClip ??
                throw new ArgumentNullException(nameof(referenceEvidenceClip));
        }

        public int ContractVersion { get; }
        public string TestName { get; }
        public string Json { get; }
        public byte[] StatePayload { get; }
        public byte[] InputTrace { get; }
        public int StateByteCount => StatePayload.Length;
        public int InputTraceByteCount => InputTrace.Length;
        public string StateSha256 { get; }
        public string InputTraceSha256 { get; }
        public TestEvidenceClip ReferenceEvidenceClip { get; }
    }

    internal sealed class RecordedTestReplayMetadata
    {
        public RecordedTestReplayMetadata(
            string unityRelease,
            string inputSystemVersion)
        {
            UnityRelease = Required(unityRelease, nameof(unityRelease));
            InputSystemVersion = Required(inputSystemVersion, nameof(inputSystemVersion));
        }

        public string UnityRelease { get; }
        public string InputSystemVersion { get; }

        static string Required(string value, string parameter)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Replay compatibility metadata is incomplete.", parameter);
            return value.Trim();
        }
    }

    /// <summary>
    /// Converts the session-only recorded draft into replayable definition v3.
    /// Binary state and trace data remain separate and in memory.
    /// </summary>
    internal static class RecordedTestDefinitionSerializer
    {
        public const int ContractVersion = 3;
        public const string StateArtifactRef = "state-payload";
        public const string TraceArtifactRef = "input-trace";

        public static RecordedTestDefinitionUpload Serialize(RecordedTestDraft draft)
        {
            var environment = new SavedTestUnityEnvironment();
            return Serialize(
                draft,
                new RecordedTestReplayMetadata(
                    environment.UnityRelease,
                    environment.InputSystemVersion));
        }

        internal static RecordedTestDefinitionUpload Serialize(
            RecordedTestDraft draft,
            RecordedTestReplayMetadata replayMetadata)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            if (replayMetadata == null)
                throw new ArgumentNullException(nameof(replayMetadata));
            if (draft.ReferenceEvidenceClip == null)
                throw new InvalidOperationException(
                    "This draft has no captured reference video. Record a new demonstration before saving.");

            byte[] statePayload = draft.Anchor.GetPayloadCopy();
            byte[] inputTrace = draft.InputTrace.GetBytesCopy();
            string stateSha256 = Sha256(statePayload);
            string inputTraceSha256 = Sha256(inputTrace);
            var json = new StringBuilder(2048);
            json.Append('{');
            Number(json, "contractVersion", ContractVersion, first: true);
            String(json, "idempotencyKey", draft.DraftId);
            json.Append(",\"test\":{");
            String(json, "name", draft.Name, first: true);
            String(
                json,
                "status",
                draft.ValidationHistory.Count > 0 ? "validating" : "draft");
            json.Append('}');

            json.Append(",\"artifacts\":[");
            Artifact(
                json,
                StateArtifactRef,
                "state_payload",
                statePayload,
                stateSha256,
                first: true);
            Artifact(
                json,
                TraceArtifactRef,
                "input_trace",
                inputTrace,
                inputTraceSha256);
            json.Append(']');

            json.Append(",\"startingState\":{");
            String(json, "providerId", draft.Anchor.ProviderId, first: true);
            Number(json, "formatVersion", draft.Anchor.StateFormatVersion);
            String(json, "label", draft.Anchor.StateLabel);
            String(json, "activeScenePath", draft.Anchor.ActiveScenePath);
            String(
                json,
                "capturedAt",
                draft.Anchor.CapturedAtUtc.ToUniversalTime()
                    .ToString("O", CultureInfo.InvariantCulture));
            String(json, "payloadArtifactRef", StateArtifactRef);
            json.Append('}');

            json.Append(",\"stages\":[");
            StateStage(json, first: true);
            TraceStage(
                json,
                draft.InputTrace.Metrics,
                replayMetadata.UnityRelease,
                replayMetadata.InputSystemVersion);
            if (draft.ExpectedObservation == null)
                HumanCheckStage(json, draft.ExpectedOutcome);
            else
                OutcomeCheckStage(json, draft.ExpectedOutcome, draft.ExpectedObservation);
            json.Append("]}");

            return new RecordedTestDefinitionUpload(
                ContractVersion,
                draft.Name,
                json.ToString(),
                statePayload,
                inputTrace,
                stateSha256,
                inputTraceSha256,
                draft.ReferenceEvidenceClip);
        }

        static void Artifact(
            StringBuilder json,
            string clientRef,
            string kind,
            byte[] bytes,
            string sha256,
            bool first = false)
        {
            if (!first) json.Append(',');
            json.Append('{');
            String(json, "clientRef", clientRef, first: true);
            String(json, "kind", kind);
            Number(json, "byteCount", bytes.Length);
            String(json, "sha256", sha256);
            json.Append('}');
        }

        static void StateStage(StringBuilder json, bool first = false)
        {
            if (!first) json.Append(',');
            json.Append('{');
            String(json, "role", "setup", first: true);
            String(json, "executor", "scripted");
            String(json, "adapter", "qamel.unity.restore_starting_state");
            json.Append(",\"configuration\":{}");
            json.Append('}');
        }

        static void TraceStage(
            StringBuilder json,
            TestInputTraceMetrics metrics,
            string unityRelease,
            string inputSystemVersion)
        {
            json.Append(',');
            json.Append('{');
            String(json, "role", "action", first: true);
            String(json, "executor", "scripted");
            String(json, "adapter", "qamel.unity.replay_input_trace");
            json.Append(",\"configuration\":{");
            Number(
                json,
                "replayableInputEventCount",
                metrics.RetainedStateEventCount,
                first: true);
            Decimal(json, "recordedDurationSeconds", metrics.RetainedDurationSeconds);
            String(json, "unityRelease", unityRelease);
            String(json, "inputSystemVersion", inputSystemVersion);
            json.Append(",\"deviceLayouts\":[");
            for (var index = 0; index < metrics.DeviceLayouts.Count; index++)
            {
                if (index > 0) json.Append(',');
                AppendQuoted(json, metrics.DeviceLayouts[index]);
            }
            json.Append(']');
            json.Append('}');
            String(json, "artifactRef", TraceArtifactRef);
            json.Append('}');
        }

        static void HumanCheckStage(StringBuilder json, string expectedOutcome)
        {
            json.Append(',');
            json.Append('{');
            String(json, "role", "check", first: true);
            String(json, "executor", "human");
            String(json, "adapter", "qamel.human.review_expected_outcome");
            json.Append(",\"configuration\":{");
            String(json, "expectedOutcome", expectedOutcome, first: true);
            json.Append('}');
            json.Append('}');
        }

        static void OutcomeCheckStage(
            StringBuilder json,
            string expectedOutcome,
            TestExpectedOutcomeObservation expectedObservation)
        {
            json.Append(',');
            json.Append('{');
            String(json, "role", "check", first: true);
            String(json, "executor", "scripted");
            String(json, "adapter", "qamel.unity.compare_outcome_observation");
            json.Append(",\"configuration\":{");
            String(json, "expectedOutcome", expectedOutcome, first: true);
            String(json, "providerId", expectedObservation.ProviderId);
            Number(json, "outcomeFormatVersion", expectedObservation.FormatVersion);
            String(json, "expectedValue", expectedObservation.Observation.Value);
            String(
                json,
                "expectedSummary",
                expectedObservation.Observation.Summary ?? "");
            json.Append('}');
            json.Append('}');
        }

        static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                var hex = new StringBuilder(digest.Length * 2);
                for (var index = 0; index < digest.Length; index++)
                    hex.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        static void String(
            StringBuilder json,
            string key,
            string value,
            bool first = false)
        {
            Key(json, key, first);
            AppendQuoted(json, value);
        }

        static void Number(
            StringBuilder json,
            string key,
            long value,
            bool first = false)
        {
            Key(json, key, first);
            json.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        static void Decimal(StringBuilder json, string key, double value)
        {
            Key(json, key, first: false);
            json.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        static void Key(StringBuilder json, string key, bool first)
        {
            if (!first) json.Append(',');
            AppendQuoted(json, key);
            json.Append(':');
        }

        static void AppendQuoted(StringBuilder json, string value)
        {
            json.Append('"');
            string safe = value ?? "";
            for (var index = 0; index < safe.Length; index++)
            {
                char character = safe[index];
                switch (character)
                {
                    case '"': json.Append("\\\""); break;
                    case '\\': json.Append("\\\\"); break;
                    case '\n': json.Append("\\n"); break;
                    case '\r': json.Append("\\r"); break;
                    case '\t': json.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                        {
                            json.Append("\\u");
                            json.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            json.Append(character);
                        }
                        break;
                }
            }
            json.Append('"');
        }
    }
}
