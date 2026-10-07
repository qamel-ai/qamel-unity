using System;
using System.Globalization;
using System.Text;
using QamelCapture.TestAuthoring;
using UnityEngine;

namespace QamelCapture.Editor.TestAuthoring
{
    internal sealed class TestRunExecutionEnvironment
    {
        public TestRunExecutionEnvironment(
            string unityRelease,
            string inputSystemVersion,
            string runtimePlatform,
            string activeScenePath,
            string applicationVersion)
        {
            UnityRelease = Required(unityRelease, nameof(unityRelease));
            InputSystemVersion = Required(inputSystemVersion, nameof(inputSystemVersion));
            RuntimePlatform = Required(runtimePlatform, nameof(runtimePlatform));
            ActiveScenePath = Required(activeScenePath, nameof(activeScenePath));
            ApplicationVersion = Required(applicationVersion, nameof(applicationVersion));
        }

        public string UnityRelease { get; }
        public string InputSystemVersion { get; }
        public string RuntimePlatform { get; }
        public string ActiveScenePath { get; }
        public string ApplicationVersion { get; }

        public static TestRunExecutionEnvironment Current()
        {
            var replay = new SavedTestUnityEnvironment();
            return new TestRunExecutionEnvironment(
                replay.UnityRelease,
                replay.InputSystemVersion,
                Application.platform.ToString(),
                replay.ActiveScenePath,
                Application.version);
        }

        static string Required(string value, string parameter)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Run environment metadata is incomplete.", parameter);
            return value.Trim();
        }
    }

    /// <summary>
    /// Serializes the terminal local controller result against the exact saved
    /// Stage IDs. The service computes the idempotency checksum after parsing.
    /// </summary>
    internal static class TestRunResultSerializer
    {
        public const int ContractVersion = 1;

        public static string Serialize(
            SavedTestVersion version,
            LocalTestRun run,
            TestRunExecutionEnvironment environment)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            if (!run.IsTerminal)
                throw new ArgumentException("Only a terminal local Run can be submitted.", nameof(run));
            if (version.Stages.Count != 3)
                throw new ArgumentException(
                    "The current Unity runner requires exactly three saved Stages.",
                    nameof(version));

            var json = new StringBuilder(2048);
            json.Append('{');
            Number(json, "contractVersion", ContractVersion, first: true);
            json.Append(",\"environment\":{");
            String(json, "unityRelease", environment.UnityRelease, first: true);
            String(json, "inputSystemVersion", environment.InputSystemVersion);
            String(json, "runtimePlatform", environment.RuntimePlatform);
            String(json, "activeScenePath", environment.ActiveScenePath);
            String(json, "applicationVersion", environment.ApplicationVersion);
            json.Append('}');
            Boolean(
                json,
                "contaminated",
                run.AdapterDiagnostics?.PhysicalInputDetected == true);
            json.Append(",\"stages\":[");
            SetupStage(json, version.Stages[0], run, first: true);
            ActionStage(json, version.Stages[1], run);
            CheckStage(json, version.Stages[2], run);
            json.Append("]}");
            return json.ToString();
        }

        static void SetupStage(
            StringBuilder json,
            SavedTestStage stage,
            LocalTestRun run,
            bool first)
        {
            string status = SetupOrActionStatus(run.StateStageStatus);
            StageStart(
                json,
                stage,
                status,
                Milliseconds(run.StateDurationSeconds + run.SettleDurationSeconds),
                first);
            json.Append(",\"output\":{}");
            StageError(
                json,
                status,
                "state_" + LowerSnake(run.StateErrorCode.ToString()),
                run.Error);
            json.Append('}');
        }

        static void ActionStage(
            StringBuilder json,
            SavedTestStage stage,
            LocalTestRun run)
        {
            string status = SetupOrActionStatus(run.ActionStageStatus);
            StageStart(json, stage, status, Milliseconds(run.ActionDurationSeconds), first: false);
            var diagnostics = run.AdapterDiagnostics ?? TestInputReplayDiagnostics.Empty();
            json.Append(",\"output\":{");
            String(json, "replayMode", diagnostics.ReplayMode, first: true);
            Number(json, "replayedInputEventCount", diagnostics.ReplayedEventCount);
            Number(json, "physicalInputEventCount", diagnostics.PhysicalInputEventCount);
            json.Append('}');
            StageError(
                json,
                status,
                "input_" + LowerSnake(run.InputErrorCode.ToString()),
                run.Error);
            json.Append('}');
        }

        static void CheckStage(
            StringBuilder json,
            SavedTestStage stage,
            LocalTestRun run)
        {
            string status = CheckStatus(run.CheckStageStatus);
            StageStart(json, stage, status, 0, first: false);
            json.Append(",\"output\":{");
            var comparison = run.OutcomeComparison;
            if (comparison != null)
            {
                String(json, "expectedValue", comparison.Expected.Value, first: true);
                String(json, "observedValue", comparison.Actual.Value);
                String(json, "expectedSummary", comparison.Expected.Summary ?? "");
                String(json, "observedSummary", comparison.Actual.Summary ?? "");
            }
            else if (!string.IsNullOrWhiteSpace(run.ReviewNote))
            {
                String(json, "reviewNote", run.ReviewNote, first: true);
            }
            json.Append('}');
            StageError(
                json,
                status,
                "outcome_" + LowerSnake(run.OutcomeErrorCode.ToString()),
                run.Error);
            json.Append('}');
        }

        static void StageStart(
            StringBuilder json,
            SavedTestStage stage,
            string status,
            long durationMs,
            bool first)
        {
            if (!Guid.TryParse(stage?.StageId, out _))
                throw new ArgumentException("A saved Stage ID is missing.", nameof(stage));
            if (!first) json.Append(',');
            json.Append('{');
            String(json, "stageId", stage.StageId, first: true);
            String(json, "status", status);
            Number(json, "durationMs", durationMs);
        }

        static void StageError(
            StringBuilder json,
            string status,
            string code,
            string message)
        {
            json.Append(",\"error\":");
            if (!string.Equals(status, "error", StringComparison.Ordinal))
            {
                json.Append("null");
                return;
            }
            json.Append('{');
            String(json, "code", code, first: true);
            String(
                json,
                "message",
                string.IsNullOrWhiteSpace(message)
                    ? "The Unity Stage failed without a detailed message."
                    : message);
            json.Append('}');
        }

        static string SetupOrActionStatus(LocalTestStageStatus status)
        {
            switch (status)
            {
                case LocalTestStageStatus.Succeeded: return "succeeded";
                case LocalTestStageStatus.Error: return "error";
                case LocalTestStageStatus.Cancelled:
                case LocalTestStageStatus.Pending:
                case LocalTestStageStatus.Running:
                    return "cancelled";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(status), status, "Invalid setup or action Stage status.");
            }
        }

        static string CheckStatus(LocalTestStageStatus status)
        {
            switch (status)
            {
                case LocalTestStageStatus.Pass: return "passed";
                case LocalTestStageStatus.Fail: return "failed";
                case LocalTestStageStatus.NeedsReview: return "needs_review";
                case LocalTestStageStatus.Error: return "error";
                case LocalTestStageStatus.Cancelled:
                case LocalTestStageStatus.Pending:
                case LocalTestStageStatus.Running:
                    return "cancelled";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(status), status, "Invalid check Stage status.");
            }
        }

        static long Milliseconds(float seconds)
        {
            if (seconds <= 0 || float.IsNaN(seconds)) return 0;
            double milliseconds = Math.Round(seconds * 1000d);
            return milliseconds >= int.MaxValue ? int.MaxValue : (long)milliseconds;
        }

        static string LowerSnake(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";
            var result = new StringBuilder(value.Length + 4);
            for (var index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (index > 0 && char.IsUpper(character)) result.Append('_');
                result.Append(char.ToLowerInvariant(character));
            }
            return result.ToString();
        }

        static void String(
            StringBuilder json,
            string key,
            string value,
            bool first = false)
        {
            Key(json, key, first);
            QamelJson.AppendQuoted(json, value);
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

        static void Boolean(StringBuilder json, string key, bool value)
        {
            Key(json, key, first: false);
            json.Append(value ? "true" : "false");
        }

        static void Key(StringBuilder json, string key, bool first)
        {
            if (!first) json.Append(',');
            QamelJson.AppendQuoted(json, key);
            json.Append(':');
        }
    }
}
